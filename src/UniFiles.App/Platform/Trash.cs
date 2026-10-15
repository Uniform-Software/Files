using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UniFiles.App.Platform;

/// <summary>
/// Moves a file to the system trash (macOS Trash, Windows Recycle Bin), where it can be restored.
/// UniFiles never deletes an original for good.
/// </summary>
public static partial class Trash
{
    /// <summary>False when the file couldn't be trashed (it stays where it is).</summary>
    public static bool Move(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            if (OperatingSystem.IsMacOS())
                return MoveMac(path);
            if (OperatingSystem.IsWindows())
                return MoveWindows(path);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            Services.CrashLog.Write("Trash", ex);
        }
        return false;
    }

    // ---- macOS: [[NSFileManager defaultManager] trashItemAtURL:url resultingItemURL:nil error:nil] ----

    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [SupportedOSPlatform("macos")]
    private static bool MoveMac(string path)
    {
        var url = Send(GetClass("NSURL"), Selector("fileURLWithPath:"), Send(GetClass("NSString"), Selector("stringWithUTF8String:"), path));
        var manager = Send(GetClass("NSFileManager"), Selector("defaultManager"));
        return url != IntPtr.Zero && SendTrash(manager, Selector("trashItemAtURL:resultingItemURL:error:"), url, IntPtr.Zero, IntPtr.Zero);
    }

    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr GetClass(string name);

    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr Selector(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr Send(IntPtr receiver, IntPtr selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr Send(IntPtr receiver, IntPtr selector, string argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool SendTrash(IntPtr receiver, IntPtr selector, IntPtr url, IntPtr resultingUrl, IntPtr error);

    // ---- Windows: SHFileOperation(FO_DELETE, FOF_ALLOWUNDO) --------------------------------------

    [SupportedOSPlatform("windows")]
    private static bool MoveWindows(string path)
    {
        var operation = new ShFileOperation
        {
            Func = 3, // FO_DELETE
            From = Path.GetFullPath(path) + "\0\0",
            // FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
            Flags = 0x0040 | 0x0010 | 0x0004 | 0x0400,
        };
        return SHFileOperation(ref operation) == 0 && !operation.Aborted && !File.Exists(path);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOperation
    {
        public IntPtr Window;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOperation operation);
}
