using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using UniFiles.App.ViewModels;

namespace UniFiles.App.Views;

public partial class QueueView : UserControl
{
    public QueueView()
    {
        InitializeComponent();
    }

    /// <summary>Starts dragging the job's results, as files, to any app that accepts them.</summary>
    private async void OnDragOutPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: JobViewModel job } || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var transfer = new DataTransfer();
        foreach (var path in job.Outputs)
        {
            if (await storage.TryGetFileFromPathAsync(path) is { } file)
                transfer.Add(DataTransferItem.CreateFile(file));
        }
        if (transfer.Items.Count > 0)
            await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Copy);
    }
}
