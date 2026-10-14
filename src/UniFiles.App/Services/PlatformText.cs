using UniFiles.Core.Resources;

namespace UniFiles.App.Services;

/// <summary>
/// Texts that differ on macOS: the menu bar instead of the notification area, ⌘ instead of Ctrl,
/// ⌫ instead of Delete. Used from XAML with x:Static.
/// </summary>
public static class PlatformText
{
    private static bool Mac => OperatingSystem.IsMacOS();

    public static string KeepInTray => Mac ? Strings.Prefs_KeepInTrayMac : Strings.Prefs_KeepInTray;

    public static string EditorHelp => Mac ? Strings.Editor_HelpMac : Strings.Editor_Help;

    public static string UndoShortcut => Mac ? "⌘Z" : "Ctrl+Z";

    public static string LaunchShortcut => Mac ? "⌘↩" : "Ctrl+Enter";

    /// <summary>"Show in Finder" / "Show in Explorer": opens the folder with the file selected.</summary>
    public static string ShowInFolder => Mac ? Strings.Common_ShowInFinder
        : OperatingSystem.IsWindows() ? Strings.Common_ShowInExplorer
        : Strings.Common_OpenFolder;

    /// <summary>Label for a button opening <paramref name="path"/>: a file is shown in its folder.</summary>
    public static string ShowLabel(string? path) => path is not null && File.Exists(path) ? ShowInFolder : Strings.Common_OpenFolder;
}
