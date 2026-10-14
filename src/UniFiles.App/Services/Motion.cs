using System.Runtime.InteropServices;

namespace UniFiles.App.Services;

/// <summary>Whether the operating system asks apps to reduce animations.</summary>
public static partial class Motion
{
    private const uint SpiGetClientAreaAnimation = 0x1042;
    private const string ObjCRuntime = "/usr/lib/libobjc.A.dylib";

    public static bool SystemPrefersReducedMotion()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var enabled, 0) && !enabled;
            if (OperatingSystem.IsMacOS())
                return MacPrefersReducedMotion();
        }
        catch (Exception)
        {
            // Unknown: keep animations.
        }
        return false;
    }

    /// <summary>[NSWorkspace sharedWorkspace].accessibilityDisplayShouldReduceMotion (AppKit is loaded by Avalonia).</summary>
    private static bool MacPrefersReducedMotion()
    {
        var workspace = SendIntPtr(GetClass("NSWorkspace"), RegisterSelector("sharedWorkspace"));
        return workspace != IntPtr.Zero && SendBool(workspace, RegisterSelector("accessibilityDisplayShouldReduceMotion"));
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, [MarshalAs(UnmanagedType.Bool)] out bool value, uint winIni);

    [LibraryImport(ObjCRuntime, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr GetClass(string name);

    [LibraryImport(ObjCRuntime, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr RegisterSelector(string name);

    [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    private static partial IntPtr SendIntPtr(IntPtr receiver, IntPtr selector);

    [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool SendBool(IntPtr receiver, IntPtr selector);
}
