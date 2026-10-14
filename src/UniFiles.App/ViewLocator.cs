using Avalonia.Controls;
using Avalonia.Controls.Templates;
using UniFiles.App.ViewModels;
using UniFiles.App.Views;

namespace UniFiles.App;

/// <summary>Maps each page view model to its view. Explicit, so nothing is resolved by reflection.</summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? data) => data switch
    {
        HomeViewModel => new HomeView(),
        ActionViewModel => new ActionView(),
        QueueViewModel => new QueueView(),
        HistoryViewModel => new HistoryView(),
        PreferencesViewModel => new PreferencesView(),
        TrimEditorViewModel => new TrimEditorView(),
        PdfOrganizerViewModel => new PdfOrganizerView(),
        _ => new TextBlock { Text = data?.GetType().Name },
    };

    public bool Match(object? data) => data is PageViewModel;
}
