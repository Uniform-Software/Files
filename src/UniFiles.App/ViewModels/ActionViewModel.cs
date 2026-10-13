using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniFiles.App.Services;
using UniFiles.Core;
using UniFiles.Core.Resources;
using UniFiles.Core.Tools;
using UniFiles.Core.Tools.Video;

namespace UniFiles.App.ViewModels;

public sealed class FileItemViewModel(string path)
{
    public string Path { get; } = path;

    public string Name { get; } = System.IO.Path.GetFileName(path);

    public long Size { get; } = File.Exists(path) ? new FileInfo(path).Length : 0;

    public string SizeText => Format.Size(Size);

    public override string ToString() => $"{Name}, {SizeText}";
}

public sealed class ToolItemViewModel(IFileTool tool)
{
    public IFileTool Tool { get; } = tool;

    public ModuleInfo Module { get; } = ModuleInfo.For(tool.Module);

    public string Name => Tool.Name;

    public string Description => Tool.Description;

    public override string ToString() => $"{Name}. {Description}";
}

/// <summary>"Réglages d'une action": choose a tool, tune it, see the estimate, launch.</summary>
public partial class ActionViewModel : PageViewModel
{
    private const int MaxPreviewRows = 200;

    private readonly MainWindowViewModel _main;
    private readonly AppServices _services;
    private readonly ToolModule? _preferredModule;

    public ActionViewModel(MainWindowViewModel main, AppServices services, IReadOnlyList<string> files, ToolModule? preferredModule)
    {
        _main = main;
        _services = services;
        _preferredModule = preferredModule;
        foreach (var file in files)
            Files.Add(new FileItemViewModel(file));
        RefreshTools();
    }

    public ObservableCollection<FileItemViewModel> Files { get; } = [];

    public ObservableCollection<ToolItemViewModel> Tools { get; } = [];

    public ObservableCollection<OptionViewModel> Options { get; } = [];

    public ObservableCollection<PreviewRow> PreviewRows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTool), nameof(LaunchLabel), nameof(HasOptions))]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    private ToolItemViewModel? _selectedTool;

    [ObservableProperty]
    private string? _estimate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    private string? _warning;

    [ObservableProperty]
    private string? _previewError;

    public bool HasTool => SelectedTool is not null;

    /// <summary>"Start", or "Open the editor" for tools set up in an editor.</summary>
    public string LaunchLabel => SelectedTool?.Tool.NeedsEditor == true ? Strings.Action_OpenEditor : Strings.Action_Launch;

    // Editor-based tools are set up in their editor, not here.
    public bool HasOptions => Options.Count > 0 && SelectedTool?.Tool.NeedsEditor != true;

    public bool HasPreview => PreviewRows.Count > 0;

    public bool HasEstimate => Estimate is not null;

    public bool HasWarning => Warning is not null;

    public string FilesSummary => $"{Format.Files(Files.Count)} · {Format.Size(Files.Sum(f => f.Size))}";

    /// <summary>Shown when the files are of different kinds, so only shared actions remain.</summary>
    public bool IsMixed => Files.Select(f => FileKindDetector.Detect(f.Path)).Distinct().Count() > 1;

    partial void OnSelectedToolChanged(ToolItemViewModel? value) => BuildOptions();

    partial void OnEstimateChanged(string? value) => OnPropertyChanged(nameof(HasEstimate));

    partial void OnWarningChanged(string? value) => OnPropertyChanged(nameof(HasWarning));

    private void RefreshTools()
    {
        var previous = SelectedTool?.Tool;
        var primary = _preferredModule ?? ModuleFor(Files.Select(f => FileKindDetector.Detect(f.Path)));
        var compatible = _services.Catalog.CompatibleWith(Files.Select(f => f.Path).ToList())
            .Select(t => new ToolItemViewModel(t))
            // The module matching the files (or picked by the user) first, renaming last.
            .OrderBy(t => t.Tool.Module == primary ? 0 : t.Tool.Module == ToolModule.Rename ? 2 : 1)
            .ToList();

        Tools.Clear();
        foreach (var tool in compatible)
            Tools.Add(tool);

        SelectedTool = Tools.FirstOrDefault(t => t.Tool == previous) ?? Tools.FirstOrDefault();
        OnPropertyChanged(nameof(FilesSummary));
        OnPropertyChanged(nameof(IsMixed));
        LaunchCommand.NotifyCanExecuteChanged();
        Update();
    }

    private static ToolModule? ModuleFor(IEnumerable<FileKind> kinds) => kinds.GroupBy(k => k).MaxBy(g => g.Count())?.Key switch
    {
        FileKind.Image => ToolModule.Images,
        FileKind.Pdf => ToolModule.Pdf,
        FileKind.Video or FileKind.Audio => ToolModule.Video,
        _ => null,
    };

    private void BuildOptions()
    {
        foreach (var option in Options)
            option.ValueChanged -= OnOptionChanged;
        Options.Clear();

        if (SelectedTool is { } item)
        {
            var saved = _services.Settings.ToolSettings.GetValueOrDefault(item.Tool.Id);
            foreach (var definition in item.Tool.Options)
            {
                var option = OptionViewModel.Create(definition);
                if (saved?.TryGetValue(definition.Key, out var value) == true)
                    option.Restore(value);
                option.ValueChanged += OnOptionChanged;
                Options.Add(option);
            }
        }

        OnPropertyChanged(nameof(HasOptions));
        Warning = AppServices.MissingProgramWarning(SelectedTool?.Tool);
        Update();
    }

    private void OnOptionChanged(object? sender, EventArgs e) => Update();

    private ToolSettings CurrentSettings() =>
        new(Options.ToDictionary(o => o.Key, o => o.CurrentValue));

    /// <summary>Refreshes what depends on the settings: visible options, estimate, preview.</summary>
    private void Update()
    {
        foreach (var option in Options)
        {
            if (option.Option.VisibleWhenKey is not { } key)
                continue;
            var other = Options.FirstOrDefault(o => o.Key == key)?.CurrentValue?.ToString();
            option.IsVisible = string.Equals(other, option.Option.VisibleWhenValue ?? bool.TrueString, StringComparison.OrdinalIgnoreCase);
        }

        Estimate = null;
        PreviewRows.Clear();
        PreviewError = null;

        if (SelectedTool?.Tool is not { } tool)
        {
            OnPropertyChanged(nameof(HasPreview));
            return;
        }

        var settings = CurrentSettings();
        var inputs = Files.Select(f => f.Path).ToList();
        var before = Files.Sum(f => f.Size);

        var estimated = tool.EstimateOutputSize(inputs, settings);
        if (tool.ShowsSizeChange && estimated is { } after && after != before)
            Estimate = string.Format(Strings.Action_Estimate, Format.Gain(before, after));

        if (tool is IPreviewableTool previewable)
        {
            try
            {
                foreach (var row in previewable.Preview(inputs.Take(MaxPreviewRows).ToList(), settings))
                    PreviewRows.Add(row);
            }
            catch (ToolException ex)
            {
                PreviewError = ex.Message;
            }
        }
        OnPropertyChanged(nameof(HasPreview));
    }

    // A missing program (ffmpeg, Ghostscript) would fail every file: don't let the job start.
    private bool CanLaunch() => SelectedTool is { } tool && Files.Count >= tool.Tool.MinimumInputs && Warning is null;

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private void Launch()
    {
        if (SelectedTool?.Tool is not { } tool)
            return;

        if (tool is VideoTrimTool trim)
        {
            _main.OpenTrimEditor(trim, Files[0].Path, this);
            return;
        }

        var settings = CurrentSettings();
        _services.SaveToolSettings(tool, settings);

        _services.Queue.Enqueue(tool, Files.Select(f => f.Path).ToList(), settings, _services.OutputFolder);
        _main.ShowQueue();
    }

    [RelayCommand]
    private void RemoveFile(FileItemViewModel file)
    {
        Files.Remove(file);
        if (Files.Count == 0)
        {
            _main.GoHome();
            return;
        }
        RefreshTools();
    }

    [RelayCommand]
    private async Task AddFiles()
    {
        var added = await DialogService.PickFilesAsync(Strings.Home_PickFiles, Strings.Home_AllFiles, ["*"]);
        var known = Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in added.Where(known.Add))
            Files.Add(new FileItemViewModel(file));
        RefreshTools();
    }

    [RelayCommand]
    private void Back() => _main.GoHome();
}
