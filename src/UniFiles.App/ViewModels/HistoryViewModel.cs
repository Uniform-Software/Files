using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using UniFiles.App.Services;
using UniFiles.Core.Jobs;
using UniFiles.Core.Resources;
using UniFiles.Core.Storage;
using UniFiles.Core.Tools;

namespace UniFiles.App.ViewModels;

public sealed class HistoryItemViewModel
{
    public HistoryItemViewModel(HistoryEntry entry, ToolCatalog catalog)
    {
        // The recipe (0.3 and later) gives back the tool or chain, with its settings.
        Recipe = entry.Recipe;
        var tool = entry.Recipe is { } recipe ? PresetResolver.Resolve(catalog, recipe)?.Tool : catalog.Find(entry.ToolId);
        Tool = tool;
        FinishedAt = entry.FinishedAt;
        // Current language first; the stored name for an action that no longer exists.
        ToolName = tool?.Name ?? entry.ToolName ?? entry.ToolId;
        Module = tool is null ? null : ModuleInfo.For(tool.Module);
        DateText = entry.FinishedAt.ToString("g");
        FilesText = Format.Files(entry.FileCount);
        GainText = tool?.ShowsSizeChange != false && entry.InputBytes != entry.OutputBytes ? Format.Gain(entry.InputBytes, entry.OutputBytes) : null;
        HasErrors = entry.FailedCount > 0 || entry.Status is JobStatus.Failed;
        ErrorText = HasErrors ? string.Format(Strings.History_Failures, entry.FailedCount) : null;
        OutputDirectory = entry.OutputDirectory;
    }

    public string ToolName { get; }

    public ToolPreset? Recipe { get; }

    public IFileTool? Tool { get; }

    public DateTime FinishedAt { get; }

    /// <summary>"Do again": known recipe, tool still there, not set up in an editor.</summary>
    public bool CanRerun => Recipe is not null && Tool is { NeedsEditor: false };

    public ModuleInfo? Module { get; }

    public string DateText { get; }

    public string FilesText { get; }

    public string? GainText { get; }

    public bool HasGain => GainText is not null;

    public bool HasErrors { get; }

    public string? ErrorText { get; }

    public string OutputDirectory { get; }
}

public partial class HistoryViewModel(MainWindowViewModel main, AppServices services) : PageViewModel
{
    public ObservableCollection<HistoryItemViewModel> Entries { get; } = [];

    public bool IsEmpty => Entries.Count == 0;

    public void Refresh()
    {
        Entries.Clear();
        try
        {
            foreach (var entry in services.History.Recent(200))
                Entries.Add(new HistoryItemViewModel(entry, services.Catalog));
        }
        catch (Exception)
        {
            // Unreadable database: show the empty state.
        }
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void Clear()
    {
        services.History.Clear();
        Refresh();
    }

    [RelayCommand]
    private Task OpenFolder(HistoryItemViewModel item) => DialogService.OpenFolderAsync(item.OutputDirectory);

    /// <summary>Same action and settings on new files.</summary>
    [RelayCommand]
    private async Task Rerun(HistoryItemViewModel item)
    {
        if (item is not { Recipe: { } recipe, Tool: { } tool })
            return;
        var patterns = ToolCatalog.PatternsFor(tool.Module);
        var files = await DialogService.PickFilesAsync(Strings.History_RerunPick, ModuleInfo.For(tool.Module).Name, patterns);
        if (files.Count > 0)
            main.OpenFiles(files, recipe: (recipe, item.FinishedAt));
    }
}
