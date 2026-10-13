using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using UniFiles.App.Services;
using UniFiles.Core.Resources;
using UniFiles.Core.Tools;

namespace UniFiles.App.ViewModels;

public sealed class ModuleCardViewModel(ModuleInfo info, IEnumerable<IFileTool> tools)
{
    public ModuleInfo Info { get; } = info;

    public string Actions { get; } = string.Join(" · ", tools.Select(t => t.Name));
}

public partial class HomeViewModel : PageViewModel
{
    private readonly MainWindowViewModel _main;
    private readonly AppServices _services;

    public HomeViewModel(MainWindowViewModel main, AppServices services)
    {
        _main = main;
        _services = services;
        Modules = ModuleInfo.All.Select(m => new ModuleCardViewModel(m, services.Catalog.ForModule(m.Module))).ToList();
        Refresh();
    }

    public IReadOnlyList<ModuleCardViewModel> Modules { get; }

    public ObservableCollection<HistoryItemViewModel> Recent { get; } = [];

    public bool HasRecent => Recent.Count > 0;

    public void Refresh()
    {
        Recent.Clear();
        try
        {
            foreach (var entry in _services.History.Recent(5))
                Recent.Add(new HistoryItemViewModel(entry, _services.Catalog));
        }
        catch (Exception)
        {
            // No history available: the section simply stays empty.
        }
        OnPropertyChanged(nameof(HasRecent));
    }

    [RelayCommand]
    private async Task PickFiles()
    {
        var files = await DialogService.PickFilesAsync(Strings.Home_PickFiles, Strings.Home_AllFiles, ["*"]);
        if (files.Count > 0)
            _main.OpenFiles(files);
    }

    [RelayCommand]
    private async Task PickFolder()
    {
        var folder = await DialogService.PickFolderAsync(Strings.Home_PickFolder);
        if (folder is not null)
            _main.OpenFiles([folder]);
    }

    [RelayCommand]
    private async Task OpenModule(ModuleCardViewModel card)
    {
        var files = await DialogService.PickFilesAsync(card.Info.Name, card.Info.Name, ToolCatalog.PatternsFor(card.Info.Module));
        if (files.Count > 0)
            _main.OpenFiles(files, card.Info.Module);
    }

    [RelayCommand]
    private Task OpenRecentFolder(HistoryItemViewModel item) => DialogService.OpenFolderAsync(item.OutputDirectory);
}
