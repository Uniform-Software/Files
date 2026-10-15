using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniFiles.App.Platform;
using UniFiles.App.Services;
using UniFiles.Core.Automation;
using UniFiles.Core.Resources;
using UniFiles.Core.Tools;

namespace UniFiles.App.ViewModels;

public sealed record PresetChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record AfterChoice(AfterProcessing Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>"Automations": watched folders, each processed with a preset.</summary>
public sealed partial class AutomationsViewModel : PageViewModel
{
    private readonly AppServices _services;

    public AutomationsViewModel(AppServices services)
    {
        _services = services;
        _launchAtLogin = LoginItem.IsEnabled;
        RefreshPresets();
        foreach (var rule in services.Settings.WatchRules)
            Rules.Add(new WatchRuleViewModel(rule, this, services));
        services.PresetsChanged += (_, _) =>
        {
            RefreshPresets();
            foreach (var rule in Rules)
                rule.RefreshPreset();
        };
        services.Automations.StatusChanged += (_, id) => Rules.FirstOrDefault(r => r.Rule.Id == id)?.RefreshStatus();
    }

    public ObservableCollection<WatchRuleViewModel> Rules { get; } = [];

    public ObservableCollection<PresetChoice> Presets { get; } = [];

    public IReadOnlyList<AfterChoice> AfterChoices { get; } =
    [
        new(AfterProcessing.Keep, Strings.Auto_AfterKeep),
        new(AfterProcessing.MoveToProcessed, Strings.Auto_AfterMove),
        new(AfterProcessing.Trash, Strings.Auto_AfterTrash),
    ];

    public IReadOnlyList<OutputModeChoice> OutputModes { get; } =
    [
        new(OutputMode.Default, Strings.Output_Default),
        new(OutputMode.NextToOriginals, Strings.Output_NextToOriginals),
        new(OutputMode.DatedSubfolder, Strings.Output_Dated),
        new(OutputMode.Custom, Strings.Output_Choose),
    ];

    public bool IsEmpty => Rules.Count == 0;

    public bool HasPresets => Presets.Count > 0;

    /// <summary>A message under the "Add" button (no preset yet, limit reached…).</summary>
    [ObservableProperty]
    private string? _notice;

    [ObservableProperty]
    private bool _launchAtLogin;

    public bool IsLoginSupported => LoginItem.IsSupported;

    partial void OnLaunchAtLoginChanged(bool value)
    {
        if (!LoginItem.Set(value))
        {
            Notice = Strings.Auto_LoginItemError;
            return;
        }
        _services.Settings.LaunchAtLogin = value;
        _services.SaveSettings();
    }

    private void RefreshPresets()
    {
        Presets.Clear();
        foreach (var preset in _services.Settings.Presets)
            Presets.Add(new PresetChoice(preset.Id, preset.Name));
        OnPropertyChanged(nameof(HasPresets));
    }

    [RelayCommand]
    private async Task Add()
    {
        Notice = null;
        if (Presets.Count == 0)
        {
            Notice = Strings.Auto_NoPreset;
            return;
        }
        if (Rules.Count >= WatchRule.MaximumCount)
        {
            Notice = string.Format(Strings.Auto_Limit, WatchRule.MaximumCount);
            return;
        }
        if (await DialogService.PickFolderAsync(Strings.Auto_Folder) is not { } folder)
            return;

        var rule = new WatchRule { Folder = folder, PresetId = Presets[0].Id };
        _services.Settings.WatchRules.Add(rule);
        Rules.Add(new WatchRuleViewModel(rule, this, _services));
        Save();
        OnPropertyChanged(nameof(IsEmpty));
    }

    internal void Remove(WatchRuleViewModel rule)
    {
        _services.Settings.WatchRules.Remove(rule.Rule);
        Rules.Remove(rule);
        Save();
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Saves the rules and restarts the watchers.</summary>
    internal void Save()
    {
        _services.SaveSettings();
        _services.Automations.Refresh();
        foreach (var rule in Rules)
            rule.RefreshStatus();
    }
}

/// <summary>One watched folder.</summary>
public sealed partial class WatchRuleViewModel : ObservableObject
{
    private readonly AutomationsViewModel _owner;
    private readonly AppServices _services;
    private bool _loading = true;

    public WatchRuleViewModel(WatchRule rule, AutomationsViewModel owner, AppServices services)
    {
        Rule = rule;
        _owner = owner;
        _services = services;
        _enabled = rule.Enabled;
        _includeSubfolders = rule.IncludeSubfolders;
        _selectedAfter = owner.AfterChoices.First(a => a.Value == rule.After);
        _selectedOutputMode = owner.OutputModes.First(m => m.Mode == rule.OutputMode);
        _customOutput = rule.CustomOutput;
        RefreshPreset();
        _loading = false;
    }

    public WatchRule Rule { get; }

    public string FolderName => Path.GetFileName(Path.TrimEndingDirectorySeparator(Rule.Folder));

    public string FolderPath => Format.ShortPath(Rule.Folder);

    public IReadOnlyList<PresetChoice> Presets => _owner.Presets;

    public IReadOnlyList<AfterChoice> AfterChoices => _owner.AfterChoices;

    public IReadOnlyList<OutputModeChoice> OutputModes => _owner.OutputModes;

    [ObservableProperty]
    private PresetChoice? _selectedPreset;

    [ObservableProperty]
    private AfterChoice _selectedAfter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputDescription), nameof(IsCustomOutput))]
    private OutputModeChoice _selectedOutputMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputDescription))]
    private string? _customOutput;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private bool _includeSubfolders;

    public bool IsCustomOutput => SelectedOutputMode.Mode == OutputMode.Custom;

    public string OutputDescription => SelectedOutputMode.Mode switch
    {
        OutputMode.NextToOriginals => Strings.Output_NextToOriginalsHint,
        OutputMode.Custom => Format.ShortPath(CustomOutput ?? ""),
        OutputMode.DatedSubfolder => Format.ShortPath(Path.Combine(_services.Settings.OutputDirectory, DateTime.Now.ToString("yyyy-MM-dd"))),
        _ => Format.ShortPath(_services.Settings.OutputDirectory),
    };

    public bool HasError => Enabled && !_services.Automations.Status(Rule.Id).Available;

    /// <summary>"12 processed today · last: capture.png", "Waiting for new files", "Paused" or "Folder not found".</summary>
    public string StatusText
    {
        get
        {
            if (!Enabled)
                return Strings.Auto_Paused;
            if (SelectedPreset is null)
                return Strings.Auto_PresetMissing;
            var status = _services.Automations.Status(Rule.Id);
            if (!status.Available)
                return Strings.Auto_Missing;
            if (status.ProcessedToday == 0)
                return Strings.Auto_Waiting;
            var text = string.Format(Strings.Auto_Today, Format.Files(status.ProcessedToday));
            return status.LastFile is { } last ? $"{text} · {string.Format(Strings.Auto_Last, last)}" : text;
        }
    }

    internal void RefreshPreset()
    {
        var loading = _loading;
        _loading = true;
        SelectedPreset = _owner.Presets.FirstOrDefault(p => p.Id == Rule.PresetId);
        _loading = loading;
        RefreshStatus();
    }

    internal void RefreshStatus()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasError));
    }

    private void Changed()
    {
        if (_loading)
            return;
        _owner.Save();
    }

    partial void OnSelectedPresetChanged(PresetChoice? value)
    {
        if (value is not null)
            Rule.PresetId = value.Id;
        Changed();
    }

    partial void OnSelectedAfterChanged(AfterChoice value)
    {
        Rule.After = value.Value;
        Changed();
    }

    partial void OnSelectedOutputModeChanged(OutputModeChoice? oldValue, OutputModeChoice newValue)
    {
        if (!_loading && newValue.Mode == OutputMode.Custom && CustomOutput is null)
        {
            _ = ChooseOutputAsync(oldValue);
            return;
        }
        Rule.OutputMode = newValue.Mode;
        Changed();
    }

    [RelayCommand]
    private Task ChangeOutput() => ChooseOutputAsync(null);

    private async Task ChooseOutputAsync(OutputModeChoice? fallback)
    {
        if (await DialogService.PickFolderAsync(Strings.Output_Choose) is { } folder)
        {
            CustomOutput = folder;
            Rule.CustomOutput = folder;
            Rule.OutputMode = OutputMode.Custom;
            Changed();
        }
        else if (fallback is not null)
        {
            _loading = true;
            SelectedOutputMode = fallback;
            _loading = false;
        }
    }

    partial void OnEnabledChanged(bool value)
    {
        Rule.Enabled = value;
        Changed();
        RefreshStatus();
    }

    partial void OnIncludeSubfoldersChanged(bool value)
    {
        Rule.IncludeSubfolders = value;
        Changed();
    }

    [RelayCommand]
    private void ProcessExisting()
    {
        var count = _services.Automations.ProcessExisting(Rule.Id);
        _owner.Notice = count == 0 ? Strings.Auto_NothingThere : string.Format(Strings.Auto_ProcessingExisting, Format.Files(count));
    }

    [RelayCommand]
    private Task ShowFolder() => DialogService.OpenFolderAsync(Rule.Folder);

    [RelayCommand]
    private void Remove() => _owner.Remove(this);
}
