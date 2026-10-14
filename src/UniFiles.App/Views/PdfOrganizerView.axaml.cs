using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using UniFiles.App.ViewModels;

namespace UniFiles.App.Views;

/// <summary>Drag and drop of pages: press on a page, release over another to move it there.</summary>
public partial class PdfOrganizerView : UserControl
{
    // Movement before a press becomes a drag, so clicks on the page buttons stay clicks.
    private const double DragThreshold = 6;

    private PageTileViewModel? _dragged;
    private PageTileViewModel? _target;
    private Point _pressedAt;
    private bool _dragging;

    public PdfOrganizerView()
    {
        InitializeComponent();
        PageList.AddHandler(PointerPressedEvent, OnPressed, handledEventsToo: false);
        PageList.AddHandler(PointerMovedEvent, OnMoved, handledEventsToo: true);
        PageList.AddHandler(PointerReleasedEvent, OnReleased, handledEventsToo: true);
        PageList.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(), handledEventsToo: true);
    }

    private PdfOrganizerViewModel? ViewModel => DataContext as PdfOrganizerViewModel;

    private PageTileViewModel? PageAt(Point point) =>
        PageList.GetVisualsAt(point).Select(v => v.FindAncestorOfType<Border>(includeSelf: true))
            .Select(b => b?.DataContext).OfType<PageTileViewModel>().FirstOrDefault();

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(PageList).Properties.IsLeftButtonPressed || e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        _pressedAt = e.GetPosition(PageList);
        _dragged = PageAt(_pressedAt);
        if (_dragged is not null)
            e.Pointer.Capture(PageList);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_dragged is null)
            return;
        var point = e.GetPosition(PageList);
        if (!_dragging && Math.Abs(point.X - _pressedAt.X) + Math.Abs(point.Y - _pressedAt.Y) < DragThreshold)
            return;
        _dragging = true;
        var target = PageAt(point);
        if (target == _dragged)
            target = null;
        if (target == _target)
            return;
        if (_target is not null)
            _target.IsDropTarget = false;
        _target = target;
        if (_target is not null)
            _target.IsDropTarget = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging && _dragged is not null && _target is not null)
            ViewModel?.Move(_dragged, _target);
        e.Pointer.Capture(null);
        EndDrag();
    }

    private void EndDrag()
    {
        if (_target is not null)
            _target.IsDropTarget = false;
        _target = null;
        _dragged = null;
        _dragging = false;
    }
}
