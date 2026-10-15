using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using UniFiles.Core;
using UniFiles.Core.Resources;
using UniFiles.Core.Storage;
using UniFiles.Core.Tools;

namespace UniFiles.App.Platform;

/// <summary>
/// macOS: one Finder Quick Action per action and preset ("UniFiles · Compress"), shown in the
/// right-click menu under Quick Actions for the files it accepts. They are Automator services in
/// ~/Library/Services (no admin rights needed) running UniFiles in the background with
/// <c>--action</c> or <c>--preset</c>; the single instance gathers the files into one job.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacQuickActionsInstaller : IContextMenuInstaller
{
    // Marks the services UniFiles created, so uninstalling never touches anyone else's.
    private const string MarkerFile = "UniFiles.quickaction";
    private const string FolderUti = "public.folder";

    private static readonly Dictionary<FileKind, string> KindUtis = new()
    {
        [FileKind.Image] = "public.image",
        [FileKind.Pdf] = "com.adobe.pdf",
        [FileKind.Video] = "public.movie",
        [FileKind.Audio] = "public.audio",
    };

    // Formats a tool may accept on their own, without the rest of their kind (e.g. HEIC only).
    private static readonly Dictionary<string, string> ExtensionUtis = new()
    {
        [".heic"] = "public.heic",
        [".heif"] = "public.heif",
    };

    private static string ServicesFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Services");

    public bool IsSupported => true;

    public bool IsInstalled => Installed().Any();

    public void Install(string executablePath, ToolCatalog catalog, IReadOnlyList<ToolPreset> presets)
    {
        Uninstall(refresh: false);
        Directory.CreateDirectory(ServicesFolder);

        foreach (var tool in catalog.Tools.Where(t => t.Module != ToolModule.Rename && !t.NeedsEditor && t.AcceptedKinds.Count > 0))
            Write(tool.Name, $"--action {tool.Id}", Utis(tool), executablePath);
        foreach (var preset in presets)
        {
            if (ExplorerMenu.PresetTool(catalog, preset) is { NeedsEditor: false } tool)
                Write(preset.Name, $"--preset {preset.Id}", Utis(tool), executablePath);
        }
        // Folders: open their content in UniFiles (files already have "Open With").
        Write(Strings.ContextMenu_OpenFolder, "--open", [FolderUti], executablePath);

        RefreshServices();
    }

    public void Uninstall() => Uninstall(refresh: true);

    private void Uninstall(bool refresh)
    {
        foreach (var workflow in Installed())
            Directory.Delete(workflow, recursive: true);
        if (refresh)
            RefreshServices();
    }

    private static IEnumerable<string> Installed() =>
        Directory.Exists(ServicesFolder)
            ? Directory.EnumerateDirectories(ServicesFolder, "*.workflow").Where(w => File.Exists(Path.Combine(w, "Contents", MarkerFile)))
            : [];

    /// <summary>The file types a tool's Quick Action shows for: whole kinds, or single formats.</summary>
    private static List<string> Utis(IFileTool tool)
    {
        var utis = new List<string>();
        foreach (var (kind, extensions) in ExplorerMenu.Extensions)
        {
            var accepted = extensions.Where(e => tool.Accepts("file" + e)).ToList();
            if (accepted.Count == extensions.Length)
                utis.Add(KindUtis[kind]);
            else
                utis.AddRange(accepted.Select(e => ExtensionUtis.GetValueOrDefault(e)).OfType<string>());
        }
        return utis.Distinct().ToList();
    }

    private static void Write(string label, string arguments, IReadOnlyList<string> utis, string executablePath)
    {
        if (utis.Count == 0)
            return;
        var title = string.Format(Strings.ContextMenu_MacItem, label);
        var folder = Path.Combine(ServicesFolder, OutputFolder.Sanitize(title) + ".workflow", "Contents");
        Directory.CreateDirectory(folder);

        // In the background, so the Finder doesn't wait for UniFiles (which keeps running).
        var app = "'" + executablePath.Replace("'", "'\\''") + "'";
        var command = $"[ -x {app} ] || exit 0; nohup {app} {arguments} \"$@\" >/dev/null 2>&1 &";

        File.WriteAllText(Path.Combine(folder, "Info.plist"), InfoPlist(title, utis));
        File.WriteAllText(Path.Combine(folder, "document.wflow"), Workflow(command));
        File.WriteAllText(Path.Combine(folder, MarkerFile), arguments);
    }

    /// <summary>Asks macOS to re-read the services, so the menu changes without logging out.</summary>
    private static void RefreshServices()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("/System/Library/CoreServices/pbs", "-update")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            process?.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The menu updates at the next login instead.
        }
    }

    private static string InfoPlist(string title, IReadOnlyList<string> utis) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>NSServices</key>
          <array>
            <dict>
              <key>NSBackgroundColorName</key><string>background</string>
              <key>NSIconName</key><string>NSActionTemplate</string>
              <key>NSMenuItem</key><dict><key>default</key><string>{SecurityElement.Escape(title)}</string></dict>
              <key>NSMessage</key><string>runWorkflowAsService</string>
              <key>NSRequiredContext</key><dict><key>NSApplicationIdentifier</key><string>com.apple.finder</string></dict>
              <key>NSSendFileTypes</key>
              <array>{string.Concat(utis.Select(u => $"<string>{u}</string>"))}</array>
            </dict>
          </array>
        </dict>
        </plist>
        """;

    // A single "Run Shell Script" action receiving the selected files as arguments (Automator's own format).
    private static string Workflow(string command) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>AMApplicationBuild</key><string>534</string>
          <key>AMApplicationVersion</key><string>2.10</string>
          <key>AMDocumentVersion</key><string>2</string>
          <key>actions</key>
          <array>
            <dict>
              <key>action</key>
              <dict>
                <key>AMAccepts</key>
                <dict>
                  <key>Container</key><string>List</string>
                  <key>Optional</key><true/>
                  <key>Types</key><array><string>com.apple.cocoa.string</string></array>
                </dict>
                <key>AMActionVersion</key><string>2.0.3</string>
                <key>AMApplication</key><array><string>Automator</string></array>
                <key>AMParameterProperties</key>
                <dict>
                  <key>COMMAND_STRING</key><dict/>
                  <key>CheckedForUserDefaultShell</key><dict/>
                  <key>inputMethod</key><dict/>
                  <key>shell</key><dict/>
                  <key>source</key><dict/>
                </dict>
                <key>AMProvides</key>
                <dict>
                  <key>Container</key><string>List</string>
                  <key>Types</key><array><string>com.apple.cocoa.string</string></array>
                </dict>
                <key>ActionBundlePath</key><string>/System/Library/Automator/Run Shell Script.action</string>
                <key>ActionName</key><string>Run Shell Script</string>
                <key>ActionParameters</key>
                <dict>
                  <key>COMMAND_STRING</key><string>{SecurityElement.Escape(command)}</string>
                  <key>CheckedForUserDefaultShell</key><true/>
                  <key>inputMethod</key><integer>1</integer>
                  <key>shell</key><string>/bin/bash</string>
                  <key>source</key><string></string>
                </dict>
                <key>BundleIdentifier</key><string>com.apple.RunShellScript</string>
                <key>CFBundleVersion</key><string>2.0.3</string>
                <key>CanShowSelectedItemsWhenRun</key><false/>
                <key>CanShowWhenRun</key><true/>
                <key>Category</key><array><string>AMCategoryUtilities</string></array>
                <key>Class Name</key><string>RunShellScriptAction</string>
                <key>InputUUID</key><string>{Guid.NewGuid().ToString().ToUpperInvariant()}</string>
                <key>Keywords</key><array><string>Shell</string><string>Script</string></array>
                <key>OutputUUID</key><string>{Guid.NewGuid().ToString().ToUpperInvariant()}</string>
                <key>UUID</key><string>{Guid.NewGuid().ToString().ToUpperInvariant()}</string>
                <key>UnlocalizedApplications</key><array><string>Automator</string></array>
                <key>isViewVisible</key><integer>1</integer>
                <key>location</key><string>309.000000:305.000000</string>
                <key>nibPath</key><string>/System/Library/Automator/Run Shell Script.action/Contents/Resources/Base.lproj/main.nib</string>
              </dict>
              <key>isViewVisible</key><integer>1</integer>
            </dict>
          </array>
          <key>connectors</key><dict/>
          <key>workflowMetaData</key>
          <dict>
            <key>applicationBundleIDsByPath</key><dict/>
            <key>applicationPaths</key><array/>
            <key>inputTypeIdentifier</key><string>com.apple.Automator.fileSystemObject</string>
            <key>outputTypeIdentifier</key><string>com.apple.Automator.nothing</string>
            <key>presentationMode</key><integer>15</integer>
            <key>processesInput</key><false/>
            <key>serviceInputTypeIdentifier</key><string>com.apple.Automator.fileSystemObject</string>
            <key>serviceOutputTypeIdentifier</key><string>com.apple.Automator.nothing</string>
            <key>serviceProcessesInput</key><false/>
            <key>systemImageName</key><string>NSActionTemplate</string>
            <key>useAutomaticInputType</key><false/>
            <key>workflowTypeIdentifier</key><string>com.apple.Automator.servicesMenu</string>
          </dict>
        </dict>
        </plist>
        """;
}
