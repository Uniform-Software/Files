using Avalonia.Media;
using UniFiles.App.Platform;

namespace UniFiles.App.Views;

/// <summary>
/// Cross-platform notifier: a small UniFiles-styled window in the screen corner.
/// Native toasts (Windows notification center, macOS, libnotify) need a packaged, signed app; they can come later.
/// </summary>
public sealed class ToastNotifier : INotifier
{
    public void Show(string title, string message, IBrush? accent, Geometry? icon, bool isError, string? folder) =>
        ToastWindow.Show(title, message, accent, icon, isError, folder);
}
