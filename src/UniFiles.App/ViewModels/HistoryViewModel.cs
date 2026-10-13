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
        var tool = catalog.Find(entry.ToolId);
        ToolName = tool?.Name ?? entry.ToolId;
        Module = tool is null ? null : ModuleInfo.For(tool.Module);
        DateText = entry.FinishedAt.ToString("g");
        FilesText = Format.Files(entry.FileCount);
        GainText = tool?.ShowsSizeChange != false && entry.InputBytes != entry.OutputBytes ? Format.Gain(entry.InputBytes, entry.OutputBytes) : null;
        HasErrors = entry.FailedCount > 0 || entry.Status is JobStatus.Failed;
        ErrorText = HasErrors ? string.Format(Strings.History_Failures, entry.FailedCount) : null;
        OutputDirectory = entry.OutputDirectory;
    }

    public string ToolName { get; }

    public ModuleInfo? Module { get; }

    public string DateText { get; }

    public string FilesText { get; }

    public string? GainText { get; }

    public bool HasGain => GainText is not null;

    public bool HasErrors { get; }

    public string? ErrorText { get; }

    public string OutputDirectory { get; }
}

public partial class HistoryViewModel(AppServices services) : PageViewModel
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
}
