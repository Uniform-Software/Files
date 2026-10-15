using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace UniFiles.App.Controls;

/// <summary>
/// Module marker: the module icon, in ink, on a tile of the module color tilted 14° (the UNI sign).
/// The icon itself stays upright so it reads at a glance. With <see cref="Mosaic"/>, smaller tiles on
/// the same tilted grid surround it, fading with distance; they are drawn outside the layout bounds.
/// </summary>
public sealed class ModuleBadge : Panel
{
    public static readonly StyledProperty<IBrush?> TileProperty =
        AvaloniaProperty.Register<ModuleBadge, IBrush?>(nameof(Tile));

    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<ModuleBadge, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<ModuleBadge, double>(nameof(Size), 28);

    public static readonly StyledProperty<bool> MosaicProperty =
        AvaloniaProperty.Register<ModuleBadge, bool>(nameof(Mosaic));

    /// <summary>The mosaic comes alive: its pieces spread a little and float (a card being hovered).</summary>
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<ModuleBadge, bool>(nameof(IsActive));

    private const double TiltDegrees = 14;

    private readonly Border _tile = new() { RenderTransform = TransformOperations($"rotate({TiltDegrees}deg)") };
    private readonly Icon _icon = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly MosaicLayer _mosaic = new() { IsVisible = false };

    public ModuleBadge()
    {
        Children.Add(_mosaic);
        Children.Add(_tile);
        Children.Add(_icon);
        VerticalAlignment = VerticalAlignment.Center;
        Apply();
    }

    public IBrush? Tile
    {
        get => GetValue(TileProperty);
        set => SetValue(TileProperty, value);
    }

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public bool Mosaic
    {
        get => GetValue(MosaicProperty);
        set => SetValue(MosaicProperty, value);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TileProperty || change.Property == IconProperty || change.Property == SizeProperty)
            Apply();
        if (change.Property == MosaicProperty)
            _mosaic.IsVisible = Mosaic;
        if (change.Property == IsActiveProperty)
            _mosaic.SetActive(IsActive, TopLevel.GetTopLevel(this) is { } top && top.Classes.Contains("reducedMotion"));
    }

    private void Apply()
    {
        // The tilted tile is a bit smaller than the badge so its corners stay inside.
        var tile = Math.Round(Size * 0.82);
        Width = Height = Size;
        _tile.Width = _tile.Height = tile;
        _tile.CornerRadius = new CornerRadius(Math.Round(tile * 0.26));
        _tile.Background = Tile;
        _mosaic.Tile = Tile;
        _mosaic.Side = tile;
        _mosaic.InvalidateVisual();
        _icon.Data = Icon;
        _icon.Size = Math.Round(Size * 0.5);
        _icon.Foreground = InkBrush();
    }

    private IBrush InkBrush() =>
        Application.Current?.TryGetResource("InkBrush", null, out var value) == true && value is IBrush brush ? brush : Brushes.Black;

    private static ITransform TransformOperations(string value) => Avalonia.Media.Transformation.TransformOperations.Parse(value);

    /// <summary>Mosaic pieces, drawn beneath the main tile and outside the layout bounds.</summary>
    private sealed class MosaicLayer : Control
    {
        // Mosaic pieces in the tilted frame, in units of the main tile (its center is 0,0, its side 1):
        // center x, center y, side, opacity. Half tiles line up with its edges, quarter tiles with theirs.
        private static readonly (double X, double Y, double Side, double Opacity)[] MosaicTiles =
        [
            (0.83, -0.28, 0.44, 0.55),
            (0.83, 0.28, 0.44, 0.30),
            (1.39, -0.28, 0.44, 0.25),
            (1.25, 0.14, 0.17, 0.22),
            (1.53, 0.42, 0.17, 0.14),
            (1.81, -0.42, 0.17, 0.14),
            (-0.14, -0.69, 0.17, 0.25),
            (0.14, -0.69, 0.17, 0.16),
            (-0.69, 0.42, 0.17, 0.18),
        ];

        public IBrush? Tile { get; set; }

        /// <summary>Side of the main tile.</summary>
        public double Side { get; set; }

        // Hover: 0 = at rest, 1 = spread out; eased towards the target at each frame.
        private double _spread;
        private double _target;
        private bool _reduced;
        private bool _running;
        private TimeSpan? _start;
        private TimeSpan _now;

        public void SetActive(bool active, bool reduced)
        {
            _target = active ? 1 : 0;
            _reduced = reduced;
            if (reduced)
            {
                _spread = _target;
                InvalidateVisual();
                return;
            }
            if (!_running)
            {
                _running = true;
                TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
            }
        }

        private void OnFrame(TimeSpan time)
        {
            _start ??= time;
            _now = time - _start.Value;
            _spread += (_target - _spread) * 0.16;
            if (Math.Abs(_target - _spread) < 0.002)
                _spread = _target;
            InvalidateVisual();
            // Keeps running while spread (the pieces float), stops once back at rest.
            if (_spread > 0 || _target > 0)
                TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
            else
                _running = false;
        }

        public override void Render(DrawingContext context)
        {
            if (Tile is not { } brush)
                return;
            var center = Bounds.Size / 2;
            var frame = Matrix.CreateRotation(TiltDegrees * Math.PI / 180) * Matrix.CreateTranslation(center.Width, center.Height);
            var seconds = _reduced ? 0 : _now.TotalSeconds;
            using (context.PushTransform(frame))
            {
                for (var i = 0; i < MosaicTiles.Length; i++)
                {
                    var (x, y, size, opacity) = MosaicTiles[i];
                    // Spread: away from the main tile; float: a slow drift, each piece in its own phase.
                    var distance = Math.Sqrt(x * x + y * y);
                    var push = _spread * 0.16;
                    var px = x + x / distance * push + _spread * 0.035 * Math.Sin(seconds * 2.1 + i * 1.7);
                    var py = y + y / distance * push + _spread * 0.035 * Math.Cos(seconds * 1.8 + i * 2.3);
                    var piece = size * Side * (1 + _spread * 0.12);
                    var rect = new Rect(px * Side - piece / 2, py * Side - piece / 2, piece, piece);
                    using (context.PushOpacity(Math.Min(1, opacity * (1 + _spread * 0.6))))
                        context.DrawRectangle(brush, null, rect, piece * 0.26, piece * 0.26);
                }
            }
        }
    }
}

/// <summary>An action name on a pill of its module color, with the module icon (ink on color).</summary>
public sealed class ActionTag : Border
{
    public static readonly StyledProperty<IBrush?> TileProperty =
        AvaloniaProperty.Register<ActionTag, IBrush?>(nameof(Tile));

    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<ActionTag, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<ActionTag, string?>(nameof(Text));

    private readonly Icon _icon = new() { Size = 15, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _text = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };

    public ActionTag()
    {
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(10, 5, 12, 5);
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Left;
        Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { _icon, _text } };
        var ink = Application.Current?.TryGetResource("InkBrush", null, out var value) == true && value is IBrush brush ? brush : Brushes.Black;
        _icon.Foreground = ink;
        _text.Foreground = ink;
    }

    public IBrush? Tile
    {
        get => GetValue(TileProperty);
        set => SetValue(TileProperty, value);
    }

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TileProperty)
            Background = Tile;
        else if (change.Property == IconProperty)
            _icon.Data = Icon;
        else if (change.Property == TextProperty)
            _text.Text = Text;
    }
}
