using Avalonia.Controls;
using Avalonia.Platform;

namespace UniFiles.App.Services;

/// <summary>
/// Icon of the windows. On macOS the window icon also becomes the Dock icon, so it uses the same
/// picture as the app bundle (drawn on Apple's icon grid, with its margin) instead of the full tile.
/// </summary>
public static class AppIcon
{
    public static void Apply(Window window)
    {
        if (OperatingSystem.IsMacOS())
            window.Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://UniFiles/Assets/unifiles-macos.png")));
    }
}
