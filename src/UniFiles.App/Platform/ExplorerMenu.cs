using UniFiles.Core;
using UniFiles.Core.Storage;
using UniFiles.Core.Tools;

namespace UniFiles.App.Platform;

/// <summary>Adds UniFiles actions to the file manager's right-click menu (Explorer submenu, Finder Quick Actions).</summary>
public interface IContextMenuInstaller
{
    /// <summary>False on systems not handled yet: the preference is then shown as unavailable.</summary>
    bool IsSupported { get; }

    bool IsInstalled { get; }

    /// <summary>
    /// Installs or refreshes the menu (labels follow the current language, commands the current
    /// executable). Presets come after the tools, for the files their tool accepts.
    /// </summary>
    void Install(string executablePath, ToolCatalog catalog, IReadOnlyList<ToolPreset> presets);

    void Uninstall();
}

public static class ExplorerMenu
{
    public static IContextMenuInstaller Installer { get; } =
        OperatingSystem.IsWindows() ? new WindowsContextMenuInstaller()
        : OperatingSystem.IsMacOS() ? new MacQuickActionsInstaller()
        : new UnsupportedContextMenuInstaller();

    /// <summary>File extensions that get the menu, by kind.</summary>
    public static IReadOnlyDictionary<FileKind, string[]> Extensions { get; } = new Dictionary<FileKind, string[]>
    {
        [FileKind.Image] = [".jpg", ".jpeg", ".png", ".webp", ".avif", ".heic", ".heif", ".gif", ".tif", ".tiff", ".bmp"],
        [FileKind.Pdf] = [".pdf"],
        [FileKind.Video] = [".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv"],
        [FileKind.Audio] = [".mp3", ".wav", ".flac", ".ogg", ".opus", ".m4a", ".aac"],
    };

    /// <summary>Quick actions offered for a file extension: every tool accepting it, except renaming (it needs a pattern) and editor-based tools.</summary>
    public static IEnumerable<IFileTool> QuickActionsFor(ToolCatalog catalog, string extension) =>
        catalog.Tools.Where(t => t.Module != ToolModule.Rename && !t.NeedsEditor && t.AcceptedKinds.Count > 0 && t.Accepts("file" + extension));

    /// <summary>Presets whose tool works on a file extension, in creation order.</summary>
    public static IEnumerable<ToolPreset> PresetsFor(ToolCatalog catalog, IReadOnlyList<ToolPreset> presets, string extension) =>
        presets.Where(p => PresetTool(catalog, p) is { NeedsEditor: false } tool && tool.Accepts("file" + extension));

    /// <summary>The action a preset runs (a chain for chain presets), or null if a tool is missing.</summary>
    public static IFileTool? PresetTool(ToolCatalog catalog, ToolPreset preset) => PresetResolver.Resolve(catalog, preset)?.Tool;
}

internal sealed class UnsupportedContextMenuInstaller : IContextMenuInstaller
{
    public bool IsSupported => false;

    public bool IsInstalled => false;

    public void Install(string executablePath, ToolCatalog catalog, IReadOnlyList<ToolPreset> presets) { }

    public void Uninstall() { }
}
