using UniFiles.Core;
using UniFiles.Core.Tools;

namespace UniFiles.App.Platform;

/// <summary>Adds the "UniFiles" submenu to the file manager's right-click menu.</summary>
public interface IContextMenuInstaller
{
    /// <summary>False on systems not handled yet: the preference is then shown as unavailable.</summary>
    bool IsSupported { get; }

    bool IsInstalled { get; }

    /// <summary>Installs or refreshes the menu (labels follow the current language, commands the current executable).</summary>
    void Install(string executablePath, ToolCatalog catalog);

    void Uninstall();
}

public static class ExplorerMenu
{
    public static IContextMenuInstaller Installer { get; } =
        OperatingSystem.IsWindows() ? new WindowsContextMenuInstaller() : new UnsupportedContextMenuInstaller();

    /// <summary>File extensions that get the menu, by kind.</summary>
    public static IReadOnlyDictionary<FileKind, string[]> Extensions { get; } = new Dictionary<FileKind, string[]>
    {
        [FileKind.Image] = [".jpg", ".jpeg", ".png", ".webp", ".avif", ".heic", ".heif", ".gif", ".tif", ".tiff", ".bmp"],
        [FileKind.Pdf] = [".pdf"],
        [FileKind.Video] = [".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv"],
        [FileKind.Audio] = [".mp3", ".wav", ".flac", ".ogg", ".opus", ".m4a", ".aac"],
    };

    /// <summary>Quick actions offered for a kind of file: every tool accepting it, except renaming (it needs a pattern) and editor-based tools.</summary>
    public static IEnumerable<IFileTool> QuickActionsFor(ToolCatalog catalog, FileKind kind) =>
        catalog.Tools.Where(t => t.Module != ToolModule.Rename && !t.NeedsEditor && t.AcceptedKinds.Contains(kind));
}

internal sealed class UnsupportedContextMenuInstaller : IContextMenuInstaller
{
    public bool IsSupported => false;

    public bool IsInstalled => false;

    public void Install(string executablePath, ToolCatalog catalog) { }

    public void Uninstall() { }
}
