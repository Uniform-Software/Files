using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using UniFiles.App.ViewModels;

namespace UniFiles.App.Views;

public partial class TrimEditorView : UserControl
{
    private TrimEditorViewModel? _viewModel;

    public TrimEditorView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel = DataContext as TrimEditorViewModel;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelChanged;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        // Keyboard editing works right away: arrows move the playhead, Space plays.
        TimelineControl.Focus();
    }

    // The frame bitmap is updated in place: the image must be told to repaint.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrimEditorViewModel.FrameVersion))
            VideoImage.InvalidateVisual();
    }
}
