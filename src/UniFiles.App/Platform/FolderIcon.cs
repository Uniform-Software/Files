using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Platform;
using UniFiles.App.Services;

namespace UniFiles.App.Platform;

/// <summary>
/// Gives the output folder the UniFiles folder icon in the Finder and the Explorer. Only a folder
/// named "UniFiles" (the default Documents/UniFiles) is touched: an output folder the user picked
/// elsewhere (Desktop, Downloads) keeps its own icon.
/// </summary>
public static partial class FolderIcon
{
    private const string FolderName = "UniFiles";

    // Windows: desktop.ini points to this icon, copied into the folder (hidden) so it survives updates.
    private const string WindowsIconFile = ".unifiles-folder.ico";

    public static void Apply(string folder)
    {
        try
        {
            if (!string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)), FolderName, StringComparison.OrdinalIgnoreCase))
                return;
            Directory.CreateDirectory(folder);
            if (OperatingSystem.IsMacOS())
                ApplyMac(folder);
            else if (OperatingSystem.IsWindows())
                ApplyWindows(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EntryPointNotFoundException or DllNotFoundException)
        {
            // Only cosmetic: the folder works the same without it.
            CrashLog.Write("FolderIcon", ex);
        }
    }

    private static byte[] Asset(string name)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://UniFiles/Assets/{name}"));
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    // ---- macOS: [[NSWorkspace sharedWorkspace] setIcon:image forFile:path options:0] ----------

    private const string ObjCRuntime = "/usr/lib/libobjc.A.dylib";

    [SupportedOSPlatform("macos")]
    private static unsafe void ApplyMac(string folder)
    {
        // Already set: the Finder keeps custom folder icons in an "Icon\r" file.
        if (File.Exists(Path.Combine(folder, "Icon\r")))
            return;

        var png = Asset("unifiles-folder.png");
        fixed (byte* bytes = png)
        {
            var data = SendPtrNuint(GetClass("NSData"), Selector("dataWithBytes:length:"), (IntPtr)bytes, (nuint)png.Length);
            var image = SendPtr(Send(GetClass("NSImage"), Selector("alloc")), Selector("initWithData:"), data);
            if (image == IntPtr.Zero)
                return;
            var path = SendPtrString(GetClass("NSString"), Selector("stringWithUTF8String:"), folder);
            var workspace = Send(GetClass("NSWorkspace"), Selector("sharedWorkspace"));
            SendBool(workspace, Selector("setIcon:forFile:options:"), image, path, 0);
            SendVoid(image, Selector("release"));
        }
    }

    [LibraryImport(ObjCRuntime, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr GetClass(string name);

    [LibraryImport(ObjCRuntime, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr Selector(string name);

    [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    private static partial IntPtr Send(IntPtr receiver, IntPtr selector);

    [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    private static partial IntPtr SendPtr(IntPtr receiver, IntPtr selector, IntPtr argument);

    [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr SendPtrString(IntPtr receiver, IntPtr selector, string argument);

    [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    private static partial IntPtr SendPtrNuint(IntPtr receiver, IntPtr selector, IntPtr pointer, nuint length);

    [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool SendBool(IntPtr receiver, IntPtr selector, IntPtr image, IntPtr path, nuint options);

    [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    private static partial void SendVoid(IntPtr receiver, IntPtr selector);

    // ---- Windows: desktop.ini + read-only folder attribute ----------------------------------

    [SupportedOSPlatform("windows")]
    private static void ApplyWindows(string folder)
    {
        var ini = Path.Combine(folder, "desktop.ini");
        var icon = Path.Combine(folder, WindowsIconFile);
        if (File.Exists(ini) && File.Exists(icon))
            return;

        File.WriteAllBytes(icon, Asset("unifiles-folder.ico"));
        File.SetAttributes(icon, FileAttributes.Hidden | FileAttributes.System);
        if (File.Exists(ini))
            File.SetAttributes(ini, FileAttributes.Normal);
        File.WriteAllText(ini, $"[.ShellClassInfo]\r\nIconResource={WindowsIconFile},0\r\n", System.Text.Encoding.Unicode);
        File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
        // The Explorer only reads desktop.ini in folders marked read-only or system.
        var info = new DirectoryInfo(folder);
        info.Attributes |= FileAttributes.ReadOnly;
        // Refresh the Explorer's view of the folder.
        SHChangeNotify(0x00001000 /* SHCNE_UPDATEDIR */, 0x0005 /* SHCNF_PATHW */, folder, IntPtr.Zero);
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);
}
