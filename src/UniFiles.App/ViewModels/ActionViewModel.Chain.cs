using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniFiles.App.Services;
using UniFiles.Core;
using UniFiles.Core.Resources;
using UniFiles.Core.Tools;

namespace UniFiles.App.ViewModels;

/// <summary>A step after the first of a chain: an action and its own settings.</summary>
public sealed partial class StepViewModel : ObservableObject
{
    public StepViewModel(ToolItemViewModel tool, IEnumerable<OptionViewModel> options)
    {
        Tool = tool;
        foreach (var option in options)
            Options.Add(option);
    }

    public ToolItemViewModel Tool { get; }

    public ObservableCollection<OptionViewModel> Options { get; } = [];

    public bool HasOptions => Options.Count > 0;

    /// <summary>Position in the chain, from 2 (the first step is the chosen action).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private int _number;

    public string Title => $"{string.Format(Strings.Chain_Step, Number)} · {Tool.Name}";

    public ToolSettings CurrentSettings() => new(Options.ToDictionary(o => o.Key, o => o.CurrentValue));
}

public partial class ActionViewModel
{
    /// <summary>Steps 2 and after; the selected action and <see cref="Options"/> are step 1.</summary>
    public ObservableCollection<StepViewModel> ExtraSteps { get; } = [];

    /// <summary>Actions that can come after the last step, offered by "Add a step".</summary>
    public ObservableCollection<ToolItemViewModel> NextStepTools { get; } = [];

    [ObservableProperty]
    private bool _isChoosingStep;

    public bool HasSteps => ExtraSteps.Count > 0;

    public bool CanAddStep => SelectedTool?.Tool is { NeedsEditor: false } first
        && !(HasSteps ? ExtraSteps[^1].Tool.Tool : first).CombinesInputs
        && NextTools().Any();

    /// <summary>"Settings", or "Step 1 · Compress" once there are several steps.</summary>
    public string SettingsTitle => HasSteps && SelectedTool is { } tool
        ? $"{string.Format(Strings.Chain_Step, 1)} · {tool.Name}"
        : Strings.Action_Settings;

    /// <summary>The kind of the files, for checking which actions can follow.</summary>
    private FileKind InputKind => Files.Select(f => FileKindDetector.Detect(f.Path)).GroupBy(k => k).MaxBy(g => g.Count())?.Key ?? FileKind.Unknown;

    private List<IFileTool> ChainTools() =>
        SelectedTool?.Tool is { } first ? [first, .. ExtraSteps.Select(s => s.Tool.Tool)] : [];

    private IEnumerable<IFileTool> NextTools()
    {
        var current = ChainTools();
        if (current.Count == 0)
            return [];
        return _services.Catalog.Tools.Where(t => ChainTool.Validate([.. current, t], InputKind) is null);
    }

    /// <summary>What runs when launching: the selected action, or the chain of every step.</summary>
    private IFileTool? EffectiveTool()
    {
        if (SelectedTool?.Tool is not { } first)
            return null;
        if (!HasSteps)
            return first;
        var steps = new List<ChainStep> { new(first, ToolSettingsOnly(CurrentSettings())) };
        steps.AddRange(ExtraSteps.Select(s => new ChainStep(s.Tool.Tool, s.CurrentSettings())));
        return ChainTool.Validate(steps.Select(s => s.Tool).ToList(), InputKind) is null ? new ChainTool(steps) : null;
    }

    /// <summary>Settings without the destination keys (a chain keeps its destination itself).</summary>
    private static ToolSettings ToolSettingsOnly(ToolSettings settings) =>
        new(settings.Values.Where(p => !OutputSettings.IsReserved(p.Key)).ToDictionary(p => p.Key, p => p.Value));

    /// <summary>Settings holding only the destination (for a chain job).</summary>
    private ToolSettings OutputOnlySettings()
    {
        var settings = new ToolSettings();
        OutputSettings.Write(settings, SelectedOutputMode.Mode, CustomOutputDirectory, KeepTree);
        return settings;
    }

    [RelayCommand]
    private void StartAddingStep()
    {
        NextStepTools.Clear();
        foreach (var tool in NextTools())
            NextStepTools.Add(new ToolItemViewModel(tool));
        IsChoosingStep = NextStepTools.Count > 0;
    }

    [RelayCommand]
    private void CancelAddingStep() => IsChoosingStep = false;

    [RelayCommand]
    private void AddStep(ToolItemViewModel tool)
    {
        IsChoosingStep = false;
        AddStep(tool.Tool, _services.Settings.ToolSettings.GetValueOrDefault(tool.Tool.Id));
        ActivePreset = null;
    }

    private void AddStep(IFileTool tool, IReadOnlyDictionary<string, System.Text.Json.JsonElement>? saved)
    {
        var options = tool.Options.Select(definition =>
        {
            var option = OptionViewModel.Create(definition);
            if (saved?.TryGetValue(definition.Key, out var value) == true)
                option.Restore(value);
            option.ValueChanged += OnOptionChanged;
            return option;
        }).ToList();
        ExtraSteps.Add(new StepViewModel(new ToolItemViewModel(tool), options));
        StepsChanged();
    }

    [RelayCommand]
    private void RemoveStep(StepViewModel step)
    {
        foreach (var option in step.Options)
            option.ValueChanged -= OnOptionChanged;
        var index = ExtraSteps.IndexOf(step);
        ExtraSteps.Remove(step);
        // Steps after it may no longer fit (e.g. removing "PDF to images" before an image action).
        while (index < ExtraSteps.Count && ChainTool.Validate(ChainTools(), InputKind) is not null)
            ExtraSteps.RemoveAt(index);
        ActivePreset = null;
        StepsChanged();
    }

    [RelayCommand]
    private void MoveStepUp(StepViewModel step) => MoveStep(step, -1);

    [RelayCommand]
    private void MoveStepDown(StepViewModel step) => MoveStep(step, +1);

    private void MoveStep(StepViewModel step, int direction)
    {
        var index = ExtraSteps.IndexOf(step);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= ExtraSteps.Count)
            return;
        ExtraSteps.Move(index, target);
        // A move that makes the chain impossible is undone.
        if (ChainTool.Validate(ChainTools(), InputKind) is not null)
            ExtraSteps.Move(target, index);
        ActivePreset = null;
        StepsChanged();
    }

    private void ClearSteps()
    {
        foreach (var option in ExtraSteps.SelectMany(s => s.Options))
            option.ValueChanged -= OnOptionChanged;
        ExtraSteps.Clear();
        StepsChanged();
    }

    private void StepsChanged()
    {
        for (var i = 0; i < ExtraSteps.Count; i++)
            ExtraSteps[i].Number = i + 2;
        OnPropertyChanged(nameof(HasSteps));
        OnPropertyChanged(nameof(HasOptions));
        OnPropertyChanged(nameof(CanAddStep));
        OnPropertyChanged(nameof(SettingsTitle));
        Update();
    }
}
