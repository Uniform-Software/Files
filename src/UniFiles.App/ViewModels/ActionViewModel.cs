using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniFiles.App.Services;
using UniFiles.Core;
using UniFiles.Core.Resources;
using UniFiles.Core.Storage;
using UniFiles.Core.Tools;
using UniFiles.Core.Tools.Pdf;
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

/// <summary>A saved preset offered for the current files.</summary>
public sealed class PresetItemViewModel(ToolPreset preset, IFileTool tool)
{
    public ToolPreset Preset { get; } = preset;

    public IFileTool Tool { get; } = tool;

    public ModuleInfo Module { get; } = ModuleInfo.For(tool.Module);

    public string Name => Preset.Name;

    public string ToolName => Tool.Name;

    /// <summary>The action and its settings, for the tooltip: "Resize · Max width: 1600 px · Quality: 82 %".</summary>
    public string Details => string.Join(Environment.NewLine, [ToolName, .. Describe(Tool, Preset)]);

    public override string ToString() => $"{Name}. {ToolName}";

    private static IEnumerable<string> Describe(IFileTool tool, ToolPreset preset)
    {
        var settings = AppServices.SettingsFrom(tool, preset.Values);
        foreach (var option in tool.Options)
        {
            // Options hidden by another one (quality when compressing to a size) don't apply.
            if (option.VisibleWhenKey is { } key
                && !string.Equals(settings[key]?.ToString(), option.VisibleWhenValue ?? bool.TrueString, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var value = settings[option.Key];
            var text = option switch
            {
                ChoiceOption choice => choice.Choices.FirstOrDefault(c => c.Value == value?.ToString())?.Label,
                NumberOption number => number.Unit is null ? $"{value}" : $"{value} {number.Unit}",
                BoolOption => null,
                _ => value?.ToString() is { Length: > 0 } t ? t : null,
            };
            if (option is BoolOption && value is true)
                yield return option.Label;
            else if (text is not null)
                yield return string.Format(Strings.Preset_Detail, option.Label, text);
        }
    }
}

/// <summary>"Réglages d'une action": choose a tool or a preset, tune it, see the estimate, launch.</summary>
public partial class ActionViewModel : PageViewModel, IDisposable
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

        // Settings change in bursts (sliders): render the comparison once they settle.
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            _ = RenderComparisonAsync();
        };
        RefreshTools();
    }

    // ---- Before/after comparison (image tools) -------------------------------------------

    private readonly DispatcherTimer _previewTimer;
    private readonly string _previewFolder = Path.Combine(Path.GetTempPath(), $"unifiles-preview-{Guid.NewGuid():N}");
    private CancellationTokenSource? _previewCancellation;

    /// <summary>The file shown in the comparison (selected in the file list).</summary>
    [ObservableProperty]
    private FileItemViewModel? _comparisonFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComparison))]
    private ImagePreview? _comparison;

    [ObservableProperty]
    private bool _comparisonBusy;

    [ObservableProperty]
    private string? _comparisonError;

    /// <summary>1:1 crop of the center rather than the whole picture.</summary>
    [ObservableProperty]
    private bool _comparisonActualPixels;

    public bool HasComparison => Comparison is not null;

    public bool CanCompare => SelectedTool?.Tool is { Module: ToolModule.Images, CombinesInputs: false } && ComparisonFile is not null;

    public string? ComparisonSizes => Comparison is { } c
        ? SelectedTool?.Tool.KeepsOriginalIfLarger == true && c.AfterBytes > c.BeforeBytes
            ? $"{Format.Gain(c.BeforeBytes, c.AfterBytes)} · {Strings.Compare_KeepsOriginal}"
            : Format.Gain(c.BeforeBytes, c.AfterBytes)
        : null;

    partial void OnComparisonFileChanged(FileItemViewModel? value) => ScheduleComparison();

    partial void OnComparisonActualPixelsChanged(bool value) => ScheduleComparison();

    partial void OnComparisonChanged(ImagePreview? oldValue, ImagePreview? newValue)
    {
        oldValue?.Dispose();
        OnPropertyChanged(nameof(ComparisonSizes));
    }

    private void ScheduleComparison()
    {
        OnPropertyChanged(nameof(CanCompare));
        _previewCancellation?.Cancel();
        _previewTimer.Stop();
        if (CanCompare)
        {
            ComparisonBusy = true;
            _previewTimer.Start();
        }
        else
        {
            ComparisonBusy = false;
            Comparison = null;
        }
    }

    private async Task RenderComparisonAsync()
    {
        if (SelectedTool?.Tool is not { } tool || ComparisonFile is not { } file || !CanCompare || Warning is not null)
        {
            ComparisonBusy = false;
            return;
        }

        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        ComparisonBusy = true;
        ComparisonError = null;
        try
        {
            Directory.CreateDirectory(_previewFolder);
            var preview = await ImagePreview.CreateAsync(tool, file.Path, CurrentSettings(), _previewFolder, ComparisonActualPixels, cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                preview.Dispose();
                return;
            }
            Comparison = preview;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ToolException ex)
        {
            Comparison = null;
            ComparisonError = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or NetVips.VipsException)
        {
            Comparison = null;
            ComparisonError = Strings.Error_ImageUnreadable;
        }
        if (_previewCancellation == cancellation)
            ComparisonBusy = false;
    }

    public void Dispose()
    {
        _previewTimer.Stop();
        _previewCancellation?.Cancel();
        Comparison = null;
        try
        {
            if (Directory.Exists(_previewFolder))
                Directory.Delete(_previewFolder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still being written: the system cleans its temp folder eventually.
        }
    }

    public ObservableCollection<FileItemViewModel> Files { get; } = [];

    public ObservableCollection<ToolItemViewModel> Tools { get; } = [];

    public ObservableCollection<OptionViewModel> Options { get; } = [];

    public ObservableCollection<PreviewRow> PreviewRows { get; } = [];

    public ObservableCollection<PresetItemViewModel> Presets { get; } = [];

    public bool HasPresets => Presets.Count > 0;

    /// <summary>The preset whose values are shown, until a setting is changed.</summary>
    [ObservableProperty]
    private PresetItemViewModel? _activePreset;

    [ObservableProperty]
    private bool _isSavingPreset;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SavePresetCommand))]
    private string _presetName = "";

    /// <summary>The preset being renamed, or null when saving a new one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PresetFormLabel), nameof(PresetFormHint))]
    private PresetItemViewModel? _renamingPreset;

    [ObservableProperty]
    private string? _presetError;

    public string PresetFormLabel => RenamingPreset is null ? Strings.Preset_NameLabel : Strings.Preset_RenameLabel;

    public string PresetFormHint => RenamingPreset is null ? Strings.Preset_SaveHint : Strings.Preset_RenameHint;

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

    partial void OnSelectedToolChanged(ToolItemViewModel? value)
    {
        ActivePreset = null;
        BuildOptions();
    }

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
        RefreshPresets();
        if (ComparisonFile is null || !Files.Contains(ComparisonFile))
            ComparisonFile = Files.FirstOrDefault(f => FileKindDetector.Detect(f.Path) == FileKind.Image);
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

    private void OnOptionChanged(object? sender, EventArgs e)
    {
        if (!_applyingPreset)
            ActivePreset = null;
        Update();
    }

    private bool _applyingPreset;

    private void RefreshPresets()
    {
        Presets.Clear();
        foreach (var preset in _services.Settings.Presets)
        {
            if (Tools.FirstOrDefault(t => t.Tool.Id == preset.ToolId && !t.Tool.NeedsEditor) is { } tool)
                Presets.Add(new PresetItemViewModel(preset, tool.Tool));
        }
        OnPropertyChanged(nameof(HasPresets));
    }

    [RelayCommand]
    private void ApplyPreset(PresetItemViewModel preset)
    {
        SelectedTool = Tools.FirstOrDefault(t => t.Tool.Id == preset.Tool.Id) ?? SelectedTool;
        _applyingPreset = true;
        try
        {
            foreach (var option in Options)
            {
                if (preset.Preset.Values.TryGetValue(option.Key, out var value))
                    option.Restore(value);
            }
        }
        finally
        {
            _applyingPreset = false;
        }
        ActivePreset = preset;
        IsSavingPreset = false;
        Update();
    }

    [RelayCommand]
    private void StartSavingPreset()
    {
        RenamingPreset = null;
        PresetError = null;
        PresetName = ActivePreset?.Name ?? "";
        IsSavingPreset = true;
    }

    /// <summary>Shows the preset (its tool and settings), with its name ready to edit.</summary>
    [RelayCommand]
    private void StartRenamingPreset(PresetItemViewModel preset)
    {
        ApplyPreset(preset);
        RenamingPreset = preset;
        PresetError = null;
        PresetName = preset.Name;
        IsSavingPreset = true;
    }

    [RelayCommand]
    private void CancelSavingPreset()
    {
        IsSavingPreset = false;
        RenamingPreset = null;
    }

    private bool CanSavePreset() => !string.IsNullOrWhiteSpace(PresetName);

    [RelayCommand(CanExecute = nameof(CanSavePreset))]
    private void SavePreset()
    {
        if (SelectedTool?.Tool is not { } tool)
            return;
        var name = PresetName.Trim();
        ToolPreset saved;
        if (RenamingPreset is { } renamed)
        {
            if (!_services.RenamePreset(renamed.Preset, name))
            {
                PresetError = Strings.Preset_NameTaken;
                return;
            }
            saved = renamed.Preset;
        }
        else
        {
            saved = _services.SavePreset(tool, name, CurrentSettings());
        }
        IsSavingPreset = false;
        RenamingPreset = null;
        PresetError = null;
        RefreshPresets();
        ActivePreset = Presets.FirstOrDefault(p => p.Preset == saved);
        _main.ShowNotification(string.Format(Strings.Preset_Saved, name));
    }

    [RelayCommand]
    private void DeletePreset(PresetItemViewModel preset)
    {
        _services.DeletePreset(preset.Preset);
        if (ActivePreset == preset)
            ActivePreset = null;
        RefreshPresets();
    }

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
        ScheduleComparison();
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
        if (tool is PdfOrganizeTool organize)
        {
            _main.OpenPdfOrganizer(organize, Files[0].Path, this);
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
