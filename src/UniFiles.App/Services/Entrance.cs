using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace UniFiles.App.Services;

/// <summary>
/// Short entrance animations: a page, a part of the Preferences, the entries of a menu that
/// opens. A fade with a small move, never in the way; nothing when animations are reduced
/// (preference or system setting, the "reducedMotion" class of the window).
/// </summary>
public static class Entrance
{
    private static readonly Easing Ease = new CubicEaseOut();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, CancellationTokenSource> Running = new();

    /// <summary>
    /// Fades the control in while it moves by (<paramref name="fromX"/>, <paramref name="fromY"/>) to its place.
    /// The starting state is applied at once, before the next frame is drawn, so the content never
    /// shows in place first; playing again on the same control restarts it instead of stacking.
    /// </summary>
    public static void Play(Control control, double fromX = 0, double fromY = 6, double delayMs = 0, double durationMs = 180)
    {
        if (IsReduced(control))
            return;
        if (Running.TryGetValue(control, out var previous))
            previous.Cancel();
        var cancellation = new CancellationTokenSource();
        Running.AddOrUpdate(control, cancellation);

        if (control.RenderTransform is not TranslateTransform translate)
            control.RenderTransform = translate = new TranslateTransform();
        control.Opacity = 0;
        translate.X = fromX;
        translate.Y = fromY;

        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(durationMs),
            Delay = TimeSpan.FromMilliseconds(delayMs),
            Easing = Ease,
            FillMode = FillMode.Both,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters = { new Setter(Visual.OpacityProperty, 0d), new Setter(TranslateTransform.XProperty, fromX), new Setter(TranslateTransform.YProperty, fromY) },
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters = { new Setter(Visual.OpacityProperty, 1d), new Setter(TranslateTransform.XProperty, 0d), new Setter(TranslateTransform.YProperty, 0d) },
                },
            },
        };
        _ = Finish(control, translate, animation, cancellation);
    }

    /// <summary>Whatever happens (end, restart), the control is left visible and in place.</summary>
    private static async Task Finish(Control control, TranslateTransform translate, Animation animation, CancellationTokenSource cancellation)
    {
        try
        {
            await animation.RunAsync(control, cancellation.Token);
        }
        finally
        {
            if (!cancellation.IsCancellationRequested)
            {
                control.Opacity = 1;
                translate.X = 0;
                translate.Y = 0;
            }
        }
    }

    public static bool IsReduced(Visual visual) =>
        TopLevel.GetTopLevel(visual) is { } top && top.Classes.Contains("reducedMotion");
}
