using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using UniFiles.App.Controls;
using UniFiles.App.Services;
using UniFiles.App.ViewModels;
using UniFiles.Core.Resources;
using UniFiles.Core.Signing;

namespace UniFiles.App.Views;

/// <summary>
/// The page's signature fields (empty ones clickable), a preview of the signature where it will
/// go, and drawing a new box with the mouse.
/// </summary>
public partial class PdfSignEditorView : UserControl
{
    private const double MinimumSize = 24;

    private PdfSignEditorViewModel? _viewModel;
    private Rectangle? _drawing;
    private Point _start;
    private bool _fitted;

    public PdfSignEditorView()
    {
        InitializeComponent();
        Overlay.PointerPressed += OnOverlayPressed;
        Overlay.PointerMoved += OnOverlayMoved;
        Overlay.PointerReleased += OnOverlayReleased;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.LayoutChanged -= OnLayoutChanged;
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }
        _viewModel = DataContext as PdfSignEditorViewModel;
        if (_viewModel is not null)
        {
            _viewModel.LayoutChanged += OnLayoutChanged;
            _viewModel.PropertyChanged += OnViewModelChanged;
            ApplyZoom();
            Build();
        }
    }

    private void OnLayoutChanged(object? sender, EventArgs e)
    {
        // First page shown: the whole page in view (what is signed is seen at once), from its top.
        if (_viewModel is { } vm && !_fitted && vm.PageImage is not null)
        {
            _fitted = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (PageScroller.Bounds.Width <= 0)
                    return;
                var fit = Math.Min((PageScroller.Bounds.Width - 48) / vm.PageWidth, (PageScroller.Bounds.Height - 48) / vm.PageHeight);
                vm.Zoom = Math.Clamp(Math.Floor(fit * 20) / 20, 0.3, 1);
                PageScroller.Offset = default;
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
        Build();
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PdfPagesViewModel.Zoom))
            ApplyZoom();
    }

    private void ApplyZoom()
    {
        if (_viewModel is not null)
            Zoomer.LayoutTransform = new ScaleTransform(_viewModel.Zoom, _viewModel.Zoom);
    }

    private void OnThumbnailPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: FormPageViewModel page })
            _viewModel?.SelectPageCommand.Execute(page);
    }

    private static IBrush Brush(string key) => ModuleInfo.Resource<IBrush>(key) ?? Brushes.Gray;

    // ---- Overlay --------------------------------------------------------------------------------

    private void Build()
    {
        Overlay.Children.Clear();
        if (_viewModel is not { } vm)
            return;

        var chosen = vm.Placement is SignaturePlacement.ExistingField field ? field.Name : null;
        foreach (var signature in vm.Fields.Where(f => f.Page == vm.CurrentPage && f.Name != chosen))
            Place(FieldFrame(signature), vm.ToDisplay(signature.Page, signature.Rect));

        if (vm.PlacementOnPage is { } rect)
            Place(Preview(vm, rect), rect);
    }

    private void Place(Control control, Rect rect)
    {
        Canvas.SetLeft(control, rect.X);
        Canvas.SetTop(control, rect.Y);
        control.Width = Math.Max(rect.Width, 4);
        control.Height = Math.Max(rect.Height, 4);
        Overlay.Children.Add(control);
    }

    /// <summary>An existing signature field: empty (dashed, click to sign there) or signed (grey).</summary>
    private static Control FieldFrame(SignatureFieldViewModel field)
    {
        var frame = new Panel
        {
            Background = field.IsEmpty ? new SolidColorBrush(Color.FromArgb(0x30, 0xD7, 0xFF, 0x3D)) : new SolidColorBrush(Color.FromArgb(0x26, 0x80, 0x80, 0x80)),
            Cursor = field.IsEmpty ? new Cursor(StandardCursorType.Hand) : Cursor.Default,
            Tag = field,
        };
        frame.Children.Add(new Rectangle
        {
            Stroke = field.IsEmpty ? Brush("InkBrush") : Brushes.Gray,
            StrokeThickness = 1,
            StrokeDashArray = field.IsEmpty ? [4, 3] : null,
        });
        var label = field.IsEmpty ? string.Format(Strings.Sign_FieldEmpty, field.Name) : string.Format(Strings.Sign_FieldSigned, field.SignedBy);
        frame.Children.Add(new TextBlock
        {
            Text = field.IsEmpty ? Strings.Sign_Sign : label,
            FontSize = 11,
            Foreground = Brush("InkBrush"),
            Margin = new Thickness(5, 3),
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false,
        });
        ToolTip.SetTip(frame, label);
        AutomationProperties.SetName(frame, label);
        return frame;
    }

    /// <summary>The signature as it will show: handwriting on the left, the lines on the right.</summary>
    private static Control Preview(PdfSignEditorViewModel vm, Rect rect)
    {
        var lines = vm.PreviewLines;
        var id = vm.SelectedIdentity?.Identity.Id ?? "";
        var hand = vm.SelectedHandwriting?.Value switch
        {
            "drawn" when SigningContext.Handwriting.Drawing(id) is { } drawing => (Control)new SignaturePad
            {
                IsEditable = false,
                Ink = Brush("InkBrush"),
                IsHitTestVisible = false,
                Drawing = drawing,
            },
            "image" when SigningContext.Handwriting.ImagePath(id) is { } path => new Image { Source = new Bitmap(path), Stretch = Stretch.Uniform },
            _ => null,
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(hand is null ? "*" : lines.Count == 0 ? "*" : "45*,55*"), Margin = new Thickness(3 * PdfPagesViewModel.Scale) };
        if (hand is not null)
            grid.Children.Add(hand);
        if (lines.Count > 0)
        {
            var size = Math.Min(12 * PdfPagesViewModel.Scale, (rect.Height - 6 * PdfPagesViewModel.Scale) / (lines.Count * 1.25));
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(hand is null ? 0 : 6, 0, 0, 0) };
            foreach (var line in lines)
                text.Children.Add(new TextBlock { Text = line, FontSize = Math.Max(6, size), Foreground = Brush("InkBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(text, hand is null ? 0 : 1);
            grid.Children.Add(text);
        }

        var frame = new Panel { Background = new SolidColorBrush(Color.FromArgb(0x40, 0xD7, 0xFF, 0x3D)), IsHitTestVisible = false };
        frame.Children.Add(new Rectangle { Stroke = Brush("InkBrush"), StrokeThickness = 1.5 });
        frame.Children.Add(grid);
        AutomationProperties.SetName(frame, Strings.Sign_Preview);
        return frame;
    }

    // ---- Click a field, or draw a box ---------------------------------------------------------------

    private void OnOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is not { } vm || !e.GetCurrentPoint(Overlay).Properties.IsLeftButtonPressed)
            return;
        if ((e.Source as Control)?.FindAncestorOfType<Panel>(includeSelf: true) is { Tag: SignatureFieldViewModel field })
        {
            vm.ChooseField(field);
            e.Handled = true;
            return;
        }
        _start = e.GetPosition(Overlay);
        _drawing = new Rectangle
        {
            Stroke = Brush("InkBrush"),
            StrokeThickness = 1,
            StrokeDashArray = [4, 3],
            Fill = new SolidColorBrush(Color.FromArgb(0x40, 0xD7, 0xFF, 0x3D)),
        };
        Canvas.SetLeft(_drawing, _start.X);
        Canvas.SetTop(_drawing, _start.Y);
        Overlay.Children.Add(_drawing);
        e.Pointer.Capture(Overlay);
        e.Handled = true;
    }

    private void OnOverlayMoved(object? sender, PointerEventArgs e)
    {
        if (_drawing is null)
            return;
        var rect = new Rect(_start, e.GetPosition(Overlay)).Normalize();
        Canvas.SetLeft(_drawing, rect.X);
        Canvas.SetTop(_drawing, rect.Y);
        _drawing.Width = rect.Width;
        _drawing.Height = rect.Height;
    }

    private void OnOverlayReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        if (_drawing is null || _viewModel is not { } vm)
            return;
        var rect = new Rect(Canvas.GetLeft(_drawing), Canvas.GetTop(_drawing), _drawing.Width, _drawing.Height);
        Overlay.Children.Remove(_drawing);
        _drawing = null;
        // A click without dragging: a box of the usual size, centered on the click.
        if (rect.Width < MinimumSize || rect.Height < MinimumSize)
        {
            var size = new Size(200 * PdfPagesViewModel.Scale, 60 * PdfPagesViewModel.Scale);
            rect = new Rect(_start.X - size.Width / 2, _start.Y - size.Height / 2, size.Width, size.Height);
        }
        vm.DrawBox(rect.Intersect(new Rect(0, 0, vm.PageWidth, vm.PageHeight)));
    }
}
