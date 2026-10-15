using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using UniFiles.Core.Signing;

namespace UniFiles.App.Controls;

/// <summary>
/// A pad to sign with the mouse, trackpad or pen. Strokes are kept normalized (0–1) so the
/// signature fits any box; <see cref="Handwriting.AspectRatio"/> is the pad's width / height.
/// </summary>
public sealed class SignaturePad : Control
{
    public static readonly StyledProperty<IBrush?> InkProperty =
        AvaloniaProperty.Register<SignaturePad, IBrush?>(nameof(Ink), Brushes.Black);

    public static readonly StyledProperty<IBrush?> GuideProperty =
        AvaloniaProperty.Register<SignaturePad, IBrush?>(nameof(Guide), Brushes.Gray);

    /// <summary>The signature shown (set from a binding); drawing on the pad doesn't change it, see <see cref="Changed"/>.</summary>
    public static readonly StyledProperty<Handwriting?> DrawingProperty =
        AvaloniaProperty.Register<SignaturePad, Handwriting?>(nameof(Drawing));

    /// <summary>False to show a signature without drawing on it (previews).</summary>
    public static readonly StyledProperty<bool> IsEditableProperty =
        AvaloniaProperty.Register<SignaturePad, bool>(nameof(IsEditable), true);

    private readonly List<List<double>> _strokes = [];
    private List<double>? _current;

    static SignaturePad()
    {
        AffectsRender<SignaturePad>(InkProperty, GuideProperty, IsEditableProperty);
        FocusableProperty.OverrideDefaultValue<SignaturePad>(true);
    }

    public IBrush? Ink
    {
        get => GetValue(InkProperty);
        set => SetValue(InkProperty, value);
    }

    public IBrush? Guide
    {
        get => GetValue(GuideProperty);
        set => SetValue(GuideProperty, value);
    }

    public Handwriting? Drawing
    {
        get => GetValue(DrawingProperty);
        set => SetValue(DrawingProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DrawingProperty)
            Load(Drawing);
    }

    public bool IsEditable
    {
        get => GetValue(IsEditableProperty);
        set => SetValue(IsEditableProperty, value);
    }

    /// <summary>A stroke was finished or the pad cleared.</summary>
    public event EventHandler? Changed;

    public bool IsEmpty => _strokes.All(s => s.Count < 4);

    public Handwriting ToHandwriting() => new()
    {
        AspectRatio = Bounds.Height > 0 ? Bounds.Width / Bounds.Height : 3,
        Strokes = _strokes.Where(s => s.Count >= 4).Select(s => s.ToList()).ToList(),
    };

    public void Load(Handwriting? handwriting)
    {
        _strokes.Clear();
        if (handwriting is not null)
            _strokes.AddRange(handwriting.Strokes.Select(s => s.ToList()));
        InvalidateVisual();
    }

    public void Clear()
    {
        _strokes.Clear();
        InvalidateVisual();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEditable || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _current = [];
        _strokes.Add(_current);
        Add(e.GetPosition(this));
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_current is null)
            return;
        // Pens and trackpads send more points than drawn: keep them all, the curve smooths them.
        foreach (var point in e.GetIntermediatePoints(this))
            Add(point.Position);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_current is null)
            return;
        Add(e.GetPosition(this));
        // A click without moving makes a dot.
        if (_current.Count == 4 && _current[0] == _current[2] && _current[1] == _current[3])
            _current[2] += 0.004;
        _current = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Add(Point point)
    {
        if (_current is null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;
        var x = Math.Clamp(point.X / Bounds.Width, 0, 1);
        var y = Math.Clamp(point.Y / Bounds.Height, 0, 1);
        if (_current.Count >= 2 && Math.Abs(_current[^2] - x) < 0.002 && Math.Abs(_current[^1] - y) < 0.002)
            return;
        _current.Add(x);
        _current.Add(y);
    }

    public override void Render(DrawingContext context)
    {
        var (width, height) = (Bounds.Width, Bounds.Height);
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        // The line to sign on.
        if (IsEditable)
            context.DrawLine(new Pen(Guide, 1, new DashStyle([4, 4], 0)), new Point(width * 0.06, height * 0.78), new Point(width * 0.94, height * 0.78));

        var pen = new Pen(Ink, Math.Max(IsEditable ? 1.5 : 0.8, height * 0.035), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        foreach (var stroke in _strokes)
        {
            if (stroke.Count < 4)
                continue;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                Point P(int i) => new(stroke[2 * i] * width, stroke[2 * i + 1] * height);
                var count = stroke.Count / 2;
                g.BeginFigure(P(0), false);
                if (count == 2)
                    g.LineTo(P(1));
                for (var i = 1; i < count - 1; i++)
                {
                    var c = P(i);
                    var n = P(i + 1);
                    g.QuadraticBezierTo(c, i == count - 2 ? n : new Point((c.X + n.X) / 2, (c.Y + n.Y) / 2));
                }
                g.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geometry);
        }
    }
}
