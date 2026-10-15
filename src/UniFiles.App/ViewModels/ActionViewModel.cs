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

public sealed class FileItemViewModel(InputFile input)
{
    public InputFile Input { get; } = input;

    public string Path { get; } = input.Path;

    public string Name { get; } = System.IO.Path.GetFileName(input.Path);

    public long Size { get; } = File.Exists(input.Path) ? new FileInfo(input.Path).Length : 0;

    public string SizeText => Format.Size(Size);

    public override string ToString() => $"{Name}, {SizeText}";
}

public sealed partial class ToolItemViewModel(IFileTool tool) : ObservableObject
{
    public IFileTool Tool { get; } = tool;

    /// <summary>The open action of the accordion.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>The action screen while this action is open (its settings show under it), null otherwise.</summary>
    [ObservableProperty]
    private ActionViewModel? _host;

    public ModuleInfo Module { get; } = ModuleInfo.For(tool.Module);

    public string Name => Tool.Name;

    public string Description => Tool.Description;

    public override string ToString() => $"{Name}. {Description}";
}

public sealed record OutputModeChoice(OutputMode Mode, string Label)
{
    public override string ToString() => Label;
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
    public string Details => Tool is ChainTool chain
        ? string.Join(Environment.NewLine, chain.Steps.SelectMany((step, i) =>
            new[] { $"{i + 1}. {step.Tool.Name}" }.Concat(Describe(step.Tool, Preset.Steps[i].Values).Select(d => "    " + d))))
        : string.Join(Environment.NewLine, [ToolName, .. Describe(Tool, Preset.Values)]);

    /// <summary>"3 steps" on a chain's chip.</summary>
    public string? StepsText => Preset.IsChain ? string.Format(Strings.Chain_Steps, Preset.Steps.Count) : null;

    public override string ToString() => $"{Name}. {ToolName}";

    private static IEnumerable<string> Describe(IFileTool tool, IReadOnlyDictionary<string, System.Text.Json.JsonElement> values)
    {
        var settings = AppServices.SettingsFrom(tool, values);
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

    public ActionViewModel(MainWindowViewModel main, AppServices services, IReadOnlyList<InputFile> files, ToolModule? preferredModule)
    {
        _main = main;
        _services = services;
        _preferredModule = preferredModule;
        foreach (var file in files)
            Files.Add(new FileItemViewModel(file));
        OutputModes =
        [
            new(OutputMode.Default, Strings.Output_Default),
            new(OutputMode.NextToOriginals, Strings.Output_NextToOriginals),
            new(OutputMode.DatedSubfolder, Strings.Output_Dated),
            new(OutputMode.Custom, Strings.Output_Choose),
        ];
        _selectedOutputMode = OutputModes[0];

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

    public bool CanCompare => EffectiveTool() is { Module: ToolModule.Images, CombinesInputs: false } tool
        && tool.OutputKind(FileKind.Image) == FileKind.Image && ComparisonFile is not null;

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
        if (EffectiveTool() is not { } tool || ComparisonFile is not { } file || !CanCompare || Warning is not null)
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

    /// <summary>The action chosen for these files, first in the accordion.</summary>
    public ObservableCollection<ToolItemViewModel> DefaultTools { get; } = [];

    /// <summary>The other actions of the files' module.</summary>
    public ObservableCollection<ToolItemViewModel> RecommendedTools { get; } = [];

    /// <summary>Actions of other modules (files, signature…).</summary>
    public ObservableCollection<ToolItemViewModel> OtherTools { get; } = [];

    public bool HasRecommended => RecommendedTools.Count > 0;

    public bool CanStartSavingPreset => HasOptions && !IsSavingPreset;

    public bool HasOther => OtherTools.Count > 0;

    /// <summary>Opens an action of the accordion; the open one closes, leaving no action chosen.</summary>
    [RelayCommand]
    private void SelectTool(ToolItemViewModel tool) => SelectedTool = SelectedTool == tool ? null : tool;

    public ObservableCollection<OptionViewModel> Options { get; } = [];

    public ObservableCollection<PreviewRow> PreviewRows { get; } = [];

    public ObservableCollection<PresetItemViewModel> Presets { get; } = [];

    public bool HasPresets => Presets.Count > 0;

    /// <summary>The preset whose values are shown, until a setting is changed.</summary>
    [ObservableProperty]
    private PresetItemViewModel? _activePreset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartSavingPreset))]
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
    public string LaunchLabel => SelectedTool?.Tool switch
    {
        PdfVerifyTool => Strings.Verify_Launch,
        { NeedsEditor: true } => Strings.Action_OpenEditor,
        _ => Strings.Action_Launch,
    };

    // Editor-based tools are set up in their editor, not here.
    public bool HasOptions => (Options.Count > 0 || HasSteps) && SelectedTool?.Tool.NeedsEditor != true;

    public bool HasPreview => PreviewRows.Count > 0;

    public bool HasEstimate => Estimate is not null;

    public bool HasWarning => Warning is not null;

    public string FilesSummary => $"{Format.Files(Files.Count)} · {Format.Size(Files.Sum(f => f.Size))}";

    /// <summary>Shown when the files are of different kinds, so only shared actions remain.</summary>
    public bool IsMixed => Files.Select(f => FileKindDetector.Detect(f.Path)).Distinct().Count() > 1;

    partial void OnSelectedToolChanged(ToolItemViewModel? value)
    {
        foreach (var tool in Tools)
        {
            tool.IsSelected = tool == value;
            tool.Host = tool == value ? this : null;
        }
        ActivePreset = null;
        IsChoosingStep = false;
        BuildOptions();
        // A new first action may not fit the steps after it.
        if (HasSteps && ChainTool.Validate(ChainTools(), InputKind) is not null)
            ClearSteps();
        OnPropertyChanged(nameof(CanAddStep));
        OnPropertyChanged(nameof(SettingsTitle));
    }

    partial void OnEstimateChanged(string? value) => OnPropertyChanged(nameof(HasEstimate));

    partial void OnWarningChanged(string? value) => OnPropertyChanged(nameof(HasWarning));

    /// <summary>Tools of the recent history, most recent first (once each).</summary>
    private List<string> RecentToolIds()
    {
        try
        {
            return _services.History.Recent(50).Select(e => e.ToolId).Distinct().ToList();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            return [];
        }
    }

    private void RefreshTools()
    {
        var previous = SelectedTool?.Tool;
        var primary = _preferredModule ?? ModuleFor(Files.Select(f => FileKindDetector.Detect(f.Path)));
        // The action used last for this kind of file is the one chosen by default.
        var recent = RecentToolIds();
        var compatible = _services.Catalog.CompatibleWith(Files.Select(f => f.Path).ToList())
            .Select(t => new ToolItemViewModel(t))
            // The module matching the files (or picked by the user) first, file-level actions last.
            .OrderBy(t => t.Tool.Module == primary ? 0 : t.Tool.Module == ToolModule.Files ? 2 : 1)
            .ThenBy(t => t.Tool.Module == primary && recent.IndexOf(t.Tool.Id) is var at and >= 0 ? at : int.MaxValue)
            .ToList();

        Tools.Clear();
        foreach (var tool in compatible)
            Tools.Add(tool);
        DefaultTools.Clear();
        RecommendedTools.Clear();
        OtherTools.Clear();
        for (var i = 0; i < compatible.Count; i++)
            (i == 0 ? DefaultTools : compatible[i].Tool.Module == compatible[0].Tool.Module ? RecommendedTools : OtherTools).Add(compatible[i]);
        OnPropertyChanged(nameof(HasRecommended));
        OnPropertyChanged(nameof(HasOther));

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
            RestoreOutput(_services.LoadToolSettings(item.Tool));
        }

        OnPropertyChanged(nameof(HasOptions));
        OnPropertyChanged(nameof(CanStartSavingPreset));
        Warning = AppServices.MissingProgramWarning(SelectedTool?.Tool);
        Update();
    }

    private void OnOptionChanged(object? sender, EventArgs e)
    {
        if (!_applyingPreset)
        {
            ActivePreset = null;
            RecipeNote = null;
        }
        Update();
    }

    private bool _applyingPreset;

    private void RefreshPresets()
    {
        Presets.Clear();
        var paths = Files.Select(f => f.Path).ToList();
        foreach (var preset in _services.Settings.Presets)
        {
            if (preset.IsChain)
            {
                if (PresetResolver.Resolve(_services.Catalog, preset)?.Tool is { } chain && paths.All(chain.Accepts) && paths.Count >= chain.MinimumInputs)
                    Presets.Add(new PresetItemViewModel(preset, chain));
            }
            else if (Tools.FirstOrDefault(t => t.Tool.Id == preset.ToolId && !t.Tool.NeedsEditor) is { } tool)
            {
                Presets.Add(new PresetItemViewModel(preset, tool.Tool));
            }
        }
        OnPropertyChanged(nameof(HasPresets));
    }

    [RelayCommand]
    private void ApplyPreset(PresetItemViewModel preset)
    {
        // A chain: its first action and settings are step 1, the others become steps 2, 3…
        var first = preset.Tool is ChainTool chain ? chain.Steps[0] : null;
        var firstTool = first?.Tool ?? preset.Tool;
        var firstValues = preset.Preset.IsChain ? preset.Preset.Steps[0].Values : preset.Preset.Values;

        SelectedTool = Tools.FirstOrDefault(t => t.Tool.Id == firstTool.Id) ?? SelectedTool;
        _applyingPreset = true;
        try
        {
            ClearSteps();
            foreach (var option in Options)
            {
                if (firstValues.TryGetValue(option.Key, out var value))
                    option.Restore(value);
            }
            if (preset.Preset.IsChain)
            {
                foreach (var step in preset.Preset.Steps.Skip(1))
                {
                    if (_services.Catalog.Find(step.ToolId) is { } tool)
                        AddStep(tool, step.Values);
                }
            }
            RestoreOutput(AppServices.SettingsFrom(preset.Tool, preset.Preset.Values));
        }
        finally
        {
            _applyingPreset = false;
        }
        ActivePreset = preset;
        IsSavingPreset = false;
        Update();
    }

    /// <summary>"Settings of 15/10/2026", while the history's settings are shown unchanged.</summary>
    [ObservableProperty]
    private string? _recipeNote;

    /// <summary>History's "Do again": the action (or chain) and settings used then, if they fit these files.</summary>
    public void ApplyRecipe(ToolPreset recipe, DateTime when)
    {
        if (PresetResolver.Resolve(_services.Catalog, recipe)?.Tool is not { } tool)
            return;
        var first = tool is ChainTool chain ? chain.Steps[0].Tool : tool;
        if (Tools.All(t => t.Tool.Id != first.Id))
            return;
        ApplyPreset(new PresetItemViewModel(recipe, tool));
        ActivePreset = null;
        RecipeNote = string.Format(Strings.History_RerunSettings, when);
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
        else if (HasSteps && EffectiveTool() is ChainTool chain)
        {
            saved = _services.SaveChainPreset(name, chain.Steps, OutputOnlySettings());
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

    private ToolSettings CurrentSettings()
    {
        var settings = new ToolSettings(Options.ToDictionary(o => o.Key, o => o.CurrentValue));
        OutputSettings.Write(settings, SelectedOutputMode.Mode, CustomOutputDirectory, KeepTree);
        return settings;
    }

    // ---- Destination ("Save to") ---------------------------------------------------------------

    public IReadOnlyList<OutputModeChoice> OutputModes { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowKeepTree), nameof(OutputDescription), nameof(IsCustomOutput))]
    private OutputModeChoice _selectedOutputMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputDescription))]
    private string? _customOutputDirectory;

    [ObservableProperty]
    private bool _keepTree;

    private bool _restoringOutput;

    /// <summary>Some files come from a dropped folder: their sub-folders can be kept.</summary>
    public bool HasFolders => Files.Any(f => f.Input.RootName is not null);

    public bool ShowKeepTree => HasFolders && SelectedOutputMode.Mode != OutputMode.NextToOriginals;

    public bool IsCustomOutput => SelectedOutputMode.Mode == OutputMode.Custom;

    /// <summary>Where the results will go, in words.</summary>
    public string OutputDescription => SelectedOutputMode.Mode switch
    {
        OutputMode.NextToOriginals => Strings.Output_NextToOriginalsHint,
        OutputMode.DatedSubfolder => Format.ShortPath(Path.Combine(_services.Settings.OutputDirectory, DateTime.Now.ToString("yyyy-MM-dd"))),
        OutputMode.Custom => Format.ShortPath(CustomOutputDirectory ?? ""),
        _ => Format.ShortPath(_services.Settings.OutputDirectory),
    };

    private void RestoreOutput(ToolSettings settings)
    {
        _restoringOutput = true;
        try
        {
            var target = _services.TargetFor(settings);
            CustomOutputDirectory = target.Mode == OutputMode.Custom ? target.BaseDirectory : null;
            SelectedOutputMode = OutputModes.First(m => m.Mode == target.Mode);
            KeepTree = target.KeepTree;
        }
        finally
        {
            _restoringOutput = false;
        }
    }

    partial void OnSelectedOutputModeChanged(OutputModeChoice? oldValue, OutputModeChoice newValue)
    {
        if (_restoringOutput || newValue.Mode != OutputMode.Custom)
            return;
        // "Choose…": ask for the folder; going back to the previous choice if canceled.
        _ = ChooseOutputAsync(oldValue);
    }

    [RelayCommand]
    private Task ChangeOutputFolder() => ChooseOutputAsync(null);

    private async Task ChooseOutputAsync(OutputModeChoice? fallback)
    {
        if (await DialogService.PickFolderAsync(Strings.Output_Choose) is { } folder)
        {
            CustomOutputDirectory = folder;
            return;
        }
        if (CustomOutputDirectory is null && fallback is not null)
        {
            _restoringOutput = true;
            SelectedOutputMode = fallback;
            _restoringOutput = false;
        }
    }

    /// <summary>Refreshes what depends on the settings: visible options, estimate, preview.</summary>
    private void Update()
    {
        foreach (var options in ExtraSteps.Select(s => s.Options).Prepend(Options))
        {
            foreach (var option in options)
            {
                if (option.Option.VisibleWhenKey is not { } key)
                    continue;
                var other = options.FirstOrDefault(o => o.Key == key)?.CurrentValue?.ToString();
                option.IsVisible = string.Equals(other, option.Option.VisibleWhenValue ?? bool.TrueString, StringComparison.OrdinalIgnoreCase);
            }
        }

        Estimate = null;
        PreviewRows.Clear();
        PreviewError = null;
        // A program missing for any step would fail every file.
        Warning = HasSteps
            ? ChainTools().Select(AppServices.MissingProgramWarning).FirstOrDefault(w => w is not null)
            : AppServices.MissingProgramWarning(SelectedTool?.Tool);

        if (EffectiveTool() is not { } tool)
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
    private bool CanLaunch() => EffectiveTool() is { } tool && Files.Count >= tool.MinimumInputs && Warning is null;

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
        if (tool is PdfFormFillTool form)
        {
            _main.OpenFormEditor(form, Files[0].Path, this);
            return;
        }
        if (tool is PdfSignTool sign)
        {
            _main.OpenSignEditor(sign, Files[0].Path, this);
            return;
        }
        if (tool is PdfVerifyTool)
        {
            _ = _main.ShowSignatureReportAsync(Files.Select(f => f.Path).ToList());
            return;
        }

        var settings = CurrentSettings();
        _services.SaveToolSettings(tool, settings);

        if (HasSteps && EffectiveTool() is ChainTool chain)
        {
            // Each step's settings become that action's "last used" ones too.
            foreach (var step in chain.Steps.Skip(1))
                _services.SaveToolSettings(step.Tool, step.Settings);
            var destination = OutputOnlySettings();
            _services.Queue.Enqueue(chain, Files.Select(f => f.Input).ToList(), destination, _services.TargetFor(destination));
        }
        else
        {
            _services.Queue.Enqueue(tool, Files.Select(f => f.Input).ToList(), settings, _services.TargetFor(settings));
        }
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
            Files.Add(new FileItemViewModel(InputFile.Of(file)));
        RefreshTools();
    }

    [RelayCommand]
    private void Back() => _main.GoHome();
}
