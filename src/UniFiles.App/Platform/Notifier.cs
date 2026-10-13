using Avalonia.Media;

namespace UniFiles.App.Platform;

/// <summary>Notifications shown outside the main window, when UniFiles works in the background.</summary>
public interface INotifier
{
    /// <param name="accent">Badge color (the module's), with <paramref name="icon"/> inside.</param>
    void Show(string title, string message, IBrush? accent, Geometry? icon, bool isError, string? folder);
}
