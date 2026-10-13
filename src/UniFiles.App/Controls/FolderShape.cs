using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace UniFiles.App.Controls;

/// <summary>
/// The window silhouette: a file folder. A tab sits on the top-left (above the sidebar) and flows
/// into the body through a concave curve, like the tab of a real folder.
/// </summary>
public sealed class FolderShape : Control
{
    public static readonly StyledProperty<double> TabWidthProperty =
        AvaloniaProperty.Register<FolderShape, double>(nameof(TabWidth), 260);

    public static readonly StyledProperty<double> TabHeightProperty =
        AvaloniaProperty.Register<FolderShape, double>(nameof(TabHeight), 52);

    public static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<FolderShape, double>(nameof(CornerRadius), 24);

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<FolderShape, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> TabFillProperty =
        AvaloniaProperty.Register<FolderShape, IBrush?>(nameof(TabFill));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<FolderShape, IBrush?>(nameof(Stroke));

    static FolderShape()
    {
        AffectsRender<FolderShape>(TabWidthProperty, TabHeightProperty, CornerRadiusProperty, FillProperty, TabFillProperty, StrokeProperty);
    }

    public double TabWidth
    {
        get => GetValue(TabWidthProperty);
        set => SetValue(TabWidthProperty, value);
    }

    public double TabHeight
    {
        get => GetValue(TabHeightProperty);
        set => SetValue(TabHeightProperty, value);
    }

    public double CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>Optional different color for the tab and the sidebar column below it.</summary>
    public IBrush? TabFill
    {
        get => GetValue(TabFillProperty);
        set => SetValue(TabFillProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>The folder outline for a given size; also used to clip the window content.</summary>
    public static Geometry Outline(Size size, double tabWidth, double tabHeight, double radius)
    {
        var (w, h) = (size.Width, size.Height);
        var tabRadius = Math.Min(radius * 0.75, tabHeight / 2);
        var curve = Math.Min(radius * 0.6, tabHeight / 2);
        tabWidth = Math.Clamp(tabWidth, tabRadius * 2, w - radius - curve);

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(0, tabRadius), isFilled: true);
            // Tab: rounded top corners.
            g.ArcTo(new Point(tabRadius, 0), new Size(tabRadius, tabRadius), 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(tabWidth - tabRadius, 0));
            g.ArcTo(new Point(tabWidth, tabRadius), new Size(tabRadius, tabRadius), 0, false, SweepDirection.Clockwise);
            // Concave curve from the tab down into the body.
            g.LineTo(new Point(tabWidth, tabHeight - curve));
            g.ArcTo(new Point(tabWidth + curve, tabHeight), new Size(curve, curve), 0, false, SweepDirection.CounterClockwise);
            // Body.
            g.LineTo(new Point(w - radius, tabHeight));
            g.ArcTo(new Point(w, tabHeight + radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(w, h - radius));
            g.ArcTo(new Point(w - radius, h), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(radius, h));
            g.ArcTo(new Point(0, h - radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            g.EndFigure(isClosed: true);
        }
        return geometry;
    }

    public override void Render(DrawingContext context)
    {
        var outline = Outline(Bounds.Size, TabWidth, TabHeight, CornerRadius);
        context.DrawGeometry(Fill, null, outline);

        if (TabFill is { } tabFill)
        {
            using (context.PushGeometryClip(outline))
                context.DrawRectangle(tabFill, null, new Rect(0, 0, TabWidth, Bounds.Height));
        }

        if (Stroke is { } stroke)
            context.DrawGeometry(null, new Pen(stroke, 1), outline);
    }
}
