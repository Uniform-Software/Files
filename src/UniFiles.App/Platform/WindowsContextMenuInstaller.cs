using System.Runtime.Versioning;
using Microsoft.Win32;
using UniFiles.Core;
using UniFiles.Core.Resources;
using UniFiles.Core.Tools;

namespace UniFiles.App.Platform;

/// <summary>
/// Windows: a cascading "UniFiles" verb under HKCU\Software\Classes (no admin rights needed).
/// On Windows 11 it appears under "Show more options", as for any unpackaged app.
/// Each selected file starts UniFiles once; the single instance gathers them into one job.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsContextMenuInstaller : IContextMenuInstaller
{
    private const string ClassesRoot = @"Software\Classes";
    private const string VerbName = "UniFiles";

    // Shows "Show more options" items for any number of selected files (default limit is 15).
    private const string MultiSelectModel = "Player";

    // ECF_SEPARATORBEFORE: draws a line above "More options…".
    private const int SeparatorBefore = 0x20;

    public bool IsSupported => true;

    public bool IsInstalled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{ClassesRoot}\Directory\shell\{VerbName}");
            return key is not null;
        }
    }

    public void Install(string executablePath, ToolCatalog catalog)
    {
        Uninstall();
        var exe = Quote(executablePath);
        var icon = $"{executablePath},0";

        foreach (var (kind, extensions) in ExplorerMenu.Extensions)
        {
            var tools = ExplorerMenu.QuickActionsFor(catalog, kind).ToList();
            foreach (var extension in extensions)
            {
                using var verb = Registry.CurrentUser.CreateSubKey($@"{ClassesRoot}\SystemFileAssociations\{extension}\shell\{VerbName}");
                verb.SetValue("MUIVerb", "UniFiles");
                verb.SetValue("Icon", icon);
                verb.SetValue("SubCommands", "");
                verb.SetValue("MultiSelectModel", MultiSelectModel);

                // Sub-verbs are sorted by key name: the numeric prefix keeps the catalog order.
                for (var i = 0; i < tools.Count; i++)
                    AddCommand(verb, $"{i:00}_{tools[i].Id}", tools[i].Name, $"{exe} --action {tools[i].Id} \"%1\"", icon);
                AddCommand(verb, "99_more", Strings.ContextMenu_MoreOptions, $"{exe} --open \"%1\"", icon, SeparatorBefore);
            }
        }

        // Folders: open their content in UniFiles.
        using var folder = Registry.CurrentUser.CreateSubKey($@"{ClassesRoot}\Directory\shell\{VerbName}");
        folder.SetValue("MUIVerb", Strings.ContextMenu_OpenFolder);
        folder.SetValue("Icon", icon);
        folder.SetValue("MultiSelectModel", MultiSelectModel);
        using var command = folder.CreateSubKey("command");
        command.SetValue("", $"{exe} --open \"%1\"");
    }

    public void Uninstall()
    {
        foreach (var extension in ExplorerMenu.Extensions.Values.SelectMany(e => e))
            Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesRoot}\SystemFileAssociations\{extension}\shell\{VerbName}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesRoot}\Directory\shell\{VerbName}", throwOnMissingSubKey: false);
    }

    private static void AddCommand(RegistryKey verb, string name, string label, string commandLine, string icon, int flags = 0)
    {
        using var item = verb.CreateSubKey($@"shell\{name}");
        item.SetValue("MUIVerb", label);
        item.SetValue("Icon", icon);
        item.SetValue("MultiSelectModel", MultiSelectModel);
        if (flags != 0)
            item.SetValue("CommandFlags", flags, RegistryValueKind.DWord);
        using var command = item.CreateSubKey("command");
        command.SetValue("", commandLine);
    }

    private static string Quote(string path) => $"\"{path}\"";
}
