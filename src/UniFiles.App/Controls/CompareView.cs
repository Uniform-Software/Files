using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace UniFiles.App.Controls;

/// <summary>
/// Before/after comparison: both images fill the same frame, "before" on the left of a handle the
/// user drags (or moves with the arrow keys), "after" on the right.
/// </summary>
public sealed class CompareView : Control
{
    public static readonly StyledProperty<Bitmap?> BeforeProperty =
        AvaloniaProperty.Register<CompareView, Bitmap?>(nameof(Before));

    public static readonly StyledProperty<Bitmap?> AfterProperty =
        AvaloniaProperty.Register<CompareView, Bitmap?>(nameof(After));

    /// <summary>Position of the handle, from 0 (all "after") to 1 (all "before").</summary>
    public static readonly StyledProperty<double> SplitProperty =
        AvaloniaProperty.Register<CompareView, double>(nameof(Split), 0.5, coerce: (_, value) => Math.Clamp(value, 0, 1));

    public static readonly StyledProperty<IBrush?> HandleBrushProperty =
        AvaloniaProperty.Register<CompareView, IBrush?>(nameof(HandleBrush));

    public static readonly StyledProperty<IBrush?> HandleForegroundProperty =
        AvaloniaProperty.Register<CompareView, IBrush?>(nameof(HandleForeground));

    private bool _dragging;

    static CompareView()
    {
        AffectsRender<CompareView>(BeforeProperty, AfterProperty, SplitProperty, HandleBrushProperty, HandleForegroundProperty);
        FocusableProperty.OverrideDefaultValue<CompareView>(true);
        ClipToBoundsProperty.OverrideDefaultValue<CompareView>(true);
        CursorProperty.OverrideDefaultValue<CompareView>(new Cursor(StandardCursorType.SizeWestEast));
    }

    public Bitmap? Before
    {
        get => GetValue(BeforeProperty);
        set => SetValue(BeforeProperty, value);
    }

    public Bitmap? After
    {
        get => GetValue(AfterProperty);
        set => SetValue(AfterProperty, value);
    }

    public double Split
    {
        get => GetValue(SplitProperty);
        set => SetValue(SplitProperty, value);
    }

    public IBrush? HandleBrush
    {
        get => GetValue(HandleBrushProperty);
        set => SetValue(HandleBrushProperty, value);
    }

    public IBrush? HandleForeground
    {
        get => GetValue(HandleForegroundProperty);
        set => SetValue(HandleForegroundProperty, value);
    }

    /// <summary>Where the images are drawn: the before image's shape, centered in the control.</summary>
    private Rect Frame()
    {
        var image = Before ?? After;
        if (image is null || image.Size.Width <= 0 || image.Size.Height <= 0)
            return default;
        var scale = Math.Min(Bounds.Width / image.Size.Width, Bounds.Height / image.Size.Height);
        var size = image.Size * scale;
        return new Rect((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2, size.Width, size.Height);
    }

    public override void Render(DrawingContext context)
    {
        var frame = Frame();
        if (frame.Width <= 0)
            return;

        if (After is { } after)
            context.DrawImage(after, new Rect(after.Size), frame);
        var x = frame.X + frame.Width * Split;
        if (Before is { } before)
        {
            using (context.PushClip(new Rect(frame.X, frame.Y, x - frame.X, frame.Height)))
                context.DrawImage(before, new Rect(before.Size), frame);
        }

        // Handle: a line across the image and a round knob with two arrows.
        if (HandleBrush is not { } brush)
            return;
        context.DrawRectangle(brush, null, new Rect(x - 1, frame.Y, 2, frame.Height));
        var center = new Point(x, frame.Y + frame.Height / 2);
        context.DrawEllipse(brush, null, center, 16, 16);
        if (HandleForeground is { } foreground)
        {
            var pen = new Pen(foreground, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            var arrows = new StreamGeometry();
            using (var g = arrows.Open())
            {
                g.BeginFigure(center + new Point(-4, -5), false);
                g.LineTo(center + new Point(-9, 0));
                g.LineTo(center + new Point(-4, 5));
                g.EndFigure(false);
                g.BeginFigure(center + new Point(4, -5), false);
                g.LineTo(center + new Point(9, 0));
                g.LineTo(center + new Point(4, 5));
                g.EndFigure(false);
            }
            context.DrawGeometry(null, pen, arrows);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _dragging = true;
        e.Pointer.Capture(this);
        MoveTo(e.GetPosition(this));
        Focus();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
            MoveTo(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.1 : 0.02;
        if (e.Key == Key.Left)
            Split -= step;
        else if (e.Key == Key.Right)
            Split += step;
        else
            return;
        e.Handled = true;
    }

    private void MoveTo(Point point)
    {
        var frame = Frame();
        if (frame.Width > 0)
            Split = (point.X - frame.X) / frame.Width;
    }
}
