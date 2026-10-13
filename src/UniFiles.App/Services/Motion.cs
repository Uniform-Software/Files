using System.Runtime.InteropServices;

namespace UniFiles.App.Services;

/// <summary>Whether the operating system asks apps to reduce animations.</summary>
public static partial class Motion
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    public static bool SystemPrefersReducedMotion()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var enabled, 0) && !enabled;
        }
        catch (Exception)
        {
            // Unknown: keep animations.
        }
        return false;
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, [MarshalAs(UnmanagedType.Bool)] out bool value, uint winIni);
}
