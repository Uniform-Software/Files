using Avalonia;
using Avalonia.Controls;

namespace UniFiles.App.Controls;

public partial class Logo : UserControl
{
    public static readonly StyledProperty<bool> AnimatedProperty =
        AvaloniaProperty.Register<Logo, bool>(nameof(Animated));

    public Logo()
    {
        InitializeComponent();
    }

    /// <summary>When true, the squares drop into place each time the logo appears.</summary>
    public bool Animated
    {
        get => GetValue(AnimatedProperty);
        set => SetValue(AnimatedProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        FirstDot.Classes.Set("drop", Animated);
        SecondDot.Classes.Set("drop", Animated);
    }
}
