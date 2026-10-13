using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace UniFiles.App.Controls;

/// <summary>A passage of the video on the timeline, kept or removed.</summary>
public sealed record TimelineSegment(double Start, double End, bool Kept);

/// <summary>
/// Simplified editing timeline: a filmstrip of thumbnails, the passages (removed ones are dimmed
/// and struck through), cut marks and a playhead topped with the UNI tilted square.
/// Click or drag to move the playhead; Left/Right move it by one second (Shift: a tenth).
/// </summary>
public sealed class Timeline : Control
{
    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<Timeline, double>(nameof(Duration));

    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<Timeline, double>(nameof(Position), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<IReadOnlyList<TimelineSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<Timeline, IReadOnlyList<TimelineSegment>?>(nameof(Segments));

    public static readonly StyledProperty<IReadOnlyList<Bitmap?>?> ThumbnailsProperty =
        AvaloniaProperty.Register<Timeline, IReadOnlyList<Bitmap?>?>(nameof(Thumbnails));

    /// <summary>Bumped by the view model when thumbnails arrive, to repaint.</summary>
    public static readonly StyledProperty<int> ThumbnailVersionProperty =
        AvaloniaProperty.Register<Timeline, int>(nameof(ThumbnailVersion));

    private const double RulerHeight = 22;
    private const double StripHeight = 64;
    private const double HandleSize = 12;
    private const double Radius = 10;

    private bool _dragging;

    static Timeline()
    {
        FocusableProperty.OverrideDefaultValue<Timeline>(true);
        AffectsRender<Timeline>(DurationProperty, PositionProperty, SegmentsProperty, ThumbnailsProperty, ThumbnailVersionProperty);
    }

    public double Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public double Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public IReadOnlyList<TimelineSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public IReadOnlyList<Bitmap?>? Thumbnails
    {
        get => GetValue(ThumbnailsProperty);
        set => SetValue(ThumbnailsProperty, value);
    }

    public int ThumbnailVersion
    {
        get => GetValue(ThumbnailVersionProperty);
        set => SetValue(ThumbnailVersionProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, RulerHeight + StripHeight + 8);

    private Rect Strip => new(HandleSize / 2, RulerHeight, Math.Max(1, Bounds.Width - HandleSize), StripHeight);

    private double X(double seconds) => Strip.X + (Duration <= 0 ? 0 : seconds / Duration * Strip.Width);

    private double SecondsAt(double x) => Duration <= 0 ? 0 : Math.Clamp((x - Strip.X) / Strip.Width, 0, 1) * Duration;

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    public override void Render(DrawingContext context)
    {
        var strip = Strip;
        var text = Brush("TextBrush", Brushes.White);
        var secondary = Brush("TextSecondaryBrush", Brushes.Gray);
        var surface = Brush("SurfaceRaisedBrush", Brushes.DimGray);
        var background = Brush("BackgroundBrush", Brushes.Black);
        var lemon = Brush("LemonBrush", Brushes.YellowGreen);
        var error = Brush("ErrorBrush", Brushes.IndianRed);

        DrawRuler(context, secondary);

        // Filmstrip, clipped to rounded corners.
        var rounded = new RoundedRect(strip, Radius);
        context.DrawRectangle(surface, null, rounded);
        using (context.PushClip(rounded))
        {
            if (Thumbnails is { Count: > 0 } thumbnails)
            {
                var width = strip.Width / thumbnails.Count;
                for (var i = 0; i < thumbnails.Count; i++)
                {
                    if (thumbnails[i] is not { } bitmap)
                        continue;
                    var cell = new Rect(strip.X + i * width, strip.Y, width + 0.5, strip.Height);
                    // Cover the cell, cropping the picture's sides.
                    var scale = Math.Max(cell.Width / bitmap.Size.Width, cell.Height / bitmap.Size.Height);
                    var source = new Rect(
                        (bitmap.Size.Width - cell.Width / scale) / 2, (bitmap.Size.Height - cell.Height / scale) / 2,
                        cell.Width / scale, cell.Height / scale);
                    context.DrawImage(bitmap, source, cell);
                }
            }

            // Removed passages: dimmed, struck through.
            if (Segments is { } segments)
            {
                var dim = new SolidColorBrush(background is ISolidColorBrush b ? b.Color : Colors.Black, 0.72);
                var strike = new Pen(error, 2);
                foreach (var segment in segments.Where(s => !s.Kept))
                {
                    var rect = new Rect(X(segment.Start), strip.Y, X(segment.End) - X(segment.Start), strip.Height);
                    context.DrawRectangle(dim, null, rect);
                    context.DrawLine(strike, rect.BottomLeft + new Vector(4, -4), rect.TopRight + new Vector(-4, 4));
                }
            }
        }

        // Kept passages: lemon frame. Cuts: cream marks.
        if (Segments is { } all)
        {
            var frame = new Pen(lemon, 3);
            foreach (var segment in all.Where(s => s.Kept))
            {
                var rect = new Rect(X(segment.Start) + 1.5, strip.Y + 1.5, X(segment.End) - X(segment.Start) - 3, strip.Height - 3);
                if (rect.Width > 2)
                    context.DrawRectangle(null, frame, new RoundedRect(rect, 6));
            }
            var cut = new Pen(text, 2, lineCap: PenLineCap.Round);
            foreach (var segment in all.Skip(1))
                context.DrawLine(cut, new Point(X(segment.Start), strip.Y - 4), new Point(X(segment.Start), strip.Bottom + 4));
        }

        // Playhead with the tilted square on top.
        var x = X(Position);
        context.DrawLine(new Pen(lemon, 2), new Point(x, RulerHeight - 4), new Point(x, strip.Bottom + 6));
        var square = new Rect(x - HandleSize / 2, RulerHeight - HandleSize - 2, HandleSize, HandleSize);
        using (context.PushTransform(Matrix.CreateTranslation(-square.Center.X, -square.Center.Y)
                   * Matrix.CreateRotation(Math.PI * 14 / 180)
                   * Matrix.CreateTranslation(square.Center.X, square.Center.Y)))
        {
            context.DrawRectangle(lemon, null, new RoundedRect(square, 3));
        }

        if (IsFocused)
            context.DrawRectangle(null, new Pen(text, 1, new DashStyle([2, 2], 0)), new RoundedRect(strip.Inflate(3), Radius + 2));
    }

    /// <summary>Time labels at a "nice" step so they never overlap.</summary>
    private void DrawRuler(DrawingContext context, IBrush brush)
    {
        if (Duration <= 0)
            return;
        double[] steps = [0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600];
        var step = steps.FirstOrDefault(s => Strip.Width / (Duration / s) >= 70, 7200);
        var typeface = new Typeface(this.TryFindResource("BodyFont", out var font) && font is FontFamily family ? family : FontFamily.Default);
        for (var t = 0.0; t <= Duration + 0.001; t += step)
        {
            var label = new FormattedText(FormatTime(t, step < 1), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, brush);
            var x = Math.Clamp(X(t) - label.Width / 2, 0, Bounds.Width - label.Width);
            context.DrawText(label, new Point(x, 0));
        }
    }

    public static string FormatTime(double seconds, bool tenths = true)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        var main = time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time:mm\\:ss}" : $"{(int)time.TotalMinutes}:{time:ss}";
        return tenths ? $"{main}.{time.Milliseconds / 100}" : main;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        _dragging = true;
        e.Pointer.Capture(this);
        Position = SecondsAt(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
            Position = SecondsAt(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.1 : 1;
        switch (e.Key)
        {
            case Key.Left:
                Position = Math.Max(0, Position - step);
                break;
            case Key.Right:
                Position = Math.Min(Duration, Position + step);
                break;
            case Key.Home:
                Position = 0;
                break;
            case Key.End:
                Position = Duration;
                break;
            default:
                base.OnKeyDown(e);
                return;
        }
        e.Handled = true;
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }
}
