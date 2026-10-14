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
}
