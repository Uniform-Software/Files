using System.Globalization;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using UniFiles.App.Services;
using UniFiles.Core.Resources;

namespace UniFiles.App.Views;

public partial class SplashWindow : Window
{
    public SplashWindow() : this(reducedMotion: false)
    {
    }

    public SplashWindow(bool reducedMotion)
    {
        InitializeComponent();
        AppIcon.Apply(this);
        Classes.Set("reducedMotion", reducedMotion);
        VersionText.Text = string.Format(Strings.Prefs_Version,
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.1.0");

        if (reducedMotion)
            return;
        // Each square fades in after the delay stored in its Tag.
        var squares = this.GetLogicalDescendants().OfType<Border>().Where(b => b.Classes.Contains("art")).ToList();
        foreach (var square in squares)
            square.Opacity = 0;
        Opened += (_, _) =>
        {
            foreach (var square in squares)
            {
                var delay = square.Tag is string text && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var d) ? d : TimeSpan.Zero;
                DispatcherTimer.RunOnce(() => square.Opacity = 1, delay + TimeSpan.FromMilliseconds(50));
            }
        };
    }

    /// <summary>Shows the current loading step.</summary>
    public void SetStatus(string text) => StatusText.Text = text;

    public static bool ShouldReduceMotion(AppServices? services) =>
        services?.Settings.ReduceMotion == true || Motion.SystemPrefersReducedMotion();
}
