using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace UniFiles.App.Platform;

/// <summary>
/// Starts UniFiles in the background when the session opens (no window: it waits in the menu bar /
/// notification area), so watched folders keep working. macOS: a LaunchAgent in
/// ~/Library/LaunchAgents; Windows: the HKCU "Run" key. No admin rights needed.
/// </summary>
public static class LoginItem
{
    public const string BackgroundArgument = "--background";

    private const string MacLabel = "com.uniformsystems.unifiles.login";
    private const string WindowsRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string WindowsValue = "UniFiles";

    public static bool IsSupported => OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();

    private static string MacAgent =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", MacLabel + ".plist");

    public static bool IsEnabled
    {
        get
        {
            if (OperatingSystem.IsMacOS())
                return File.Exists(MacAgent);
            if (OperatingSystem.IsWindows())
                return IsEnabledWindows();
            return false;
        }
    }

    /// <summary>Turns it on or off; false if the system refused.</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                SetMac(enabled);
            else if (OperatingSystem.IsWindows())
                SetWindows(enabled);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            Services.CrashLog.Write("LoginItem", ex);
            return false;
        }
    }

    private static void SetMac(bool enabled)
    {
        if (!enabled)
        {
            File.Delete(MacAgent);
            return;
        }
        if (Environment.ProcessPath is not { } executable)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(MacAgent)!);
        File.WriteAllText(MacAgent, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{MacLabel}</string>
              <key>ProgramArguments</key>
              <array><string>{SecurityElement.Escape(executable)}</string><string>{BackgroundArgument}</string></array>
              <key>RunAtLoad</key><true/>
              <key>LimitLoadToSessionType</key><string>Aqua</string>
            </dict>
            </plist>
            """);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsEnabledWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(WindowsRunKey);
        return key?.GetValue(WindowsValue) is not null;
    }

    [SupportedOSPlatform("windows")]
    private static void SetWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(WindowsRunKey);
        if (enabled && Environment.ProcessPath is { } executable)
            key.SetValue(WindowsValue, $"\"{executable}\" {BackgroundArgument}");
        else
            key.DeleteValue(WindowsValue, throwOnMissingValue: false);
    }
}
