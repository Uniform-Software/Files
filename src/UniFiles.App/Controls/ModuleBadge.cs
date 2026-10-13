using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace UniFiles.App.Controls;

/// <summary>
/// Module marker: the module icon, in ink, on a tile of the module color tilted 14° (the UNI sign).
/// The icon itself stays upright so it reads at a glance.
/// </summary>
public sealed class ModuleBadge : Panel
{
    public static readonly StyledProperty<IBrush?> TileProperty =
        AvaloniaProperty.Register<ModuleBadge, IBrush?>(nameof(Tile));

    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<ModuleBadge, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<ModuleBadge, double>(nameof(Size), 28);

    private readonly Border _tile = new() { RenderTransform = TransformOperations("rotate(14deg)") };
    private readonly Icon _icon = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    public ModuleBadge()
    {
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TileProperty || change.Property == IconProperty || change.Property == SizeProperty)
            Apply();
    }

    private void Apply()
    {
        // The tilted tile is a bit smaller than the badge so its corners stay inside.
        var tile = Math.Round(Size * 0.82);
        Width = Height = Size;
        _tile.Width = _tile.Height = tile;
        _tile.CornerRadius = new CornerRadius(Math.Round(tile * 0.26));
        _tile.Background = Tile;
        _icon.Data = Icon;
        _icon.Size = Math.Round(Size * 0.5);
        _icon.Foreground = InkBrush();
    }

    private IBrush InkBrush() =>
        Application.Current?.TryGetResource("InkBrush", null, out var value) == true && value is IBrush brush ? brush : Brushes.Black;

    private static ITransform TransformOperations(string value) => Avalonia.Media.Transformation.TransformOperations.Parse(value);
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
