using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace UniFiles.App.Services;

/// <summary>File pickers and folder opening, backed by the main window.</summary>
public static class DialogService
{
    public static TopLevel? Owner { get; set; }

    public static async Task<IReadOnlyList<string>> PickFilesAsync(string title, string typeName, IReadOnlyList<string> patterns)
    {
        if (Owner is null)
            return [];
        var files = await Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = patterns is ["*"] ? null : [new FilePickerFileType(typeName) { Patterns = patterns }, FilePickerFileTypes.All],
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public static async Task<string?> PickFileAsync(string title, string typeName, IReadOnlyList<string> patterns)
    {
        if (Owner is null)
            return null;
        var files = await Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(typeName) { Patterns = patterns }, FilePickerFileTypes.All],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>"Save as": the chosen path, or null when canceled.</summary>
    public static async Task<string?> SaveFileAsync(string title, string suggestedName, string typeName, string extension)
    {
        if (Owner is null)
            return null;
        var file = await Owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(typeName) { Patterns = ["*." + extension] }],
        });
        return file?.TryGetLocalPath();
    }

    public static async Task<string?> PickFolderAsync(string title)
    {
        if (Owner is null)
            return null;
        var folders = await Owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    /// <summary>
    /// Opens the folder of a file with the file selected (Finder, Explorer); other systems just
    /// open the folder. A folder path opens the folder.
    /// </summary>
    public static async Task ShowAsync(string path, TopLevel? from = null)
    {
        if (!File.Exists(path))
        {
            await OpenFolderAsync(path, from);
            return;
        }
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open") { ArgumentList = { "-R", path }, UseShellExecute = false })?.Dispose();
                return;
            }
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();
                return;
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Fall back to the folder below.
        }
        await OpenFolderAsync(Path.GetDirectoryName(path)!, from);
    }

    /// <param name="from">Window to launch from when the main window isn't open (notifications).</param>
    public static async Task OpenFolderAsync(string path, TopLevel? from = null)
    {
        Directory.CreateDirectory(path);
        if ((from ?? Owner) is { } top)
        {
            await top.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
            return;
        }
        // No window at all (tray only): let the system open it (Explorer, Finder, xdg-open).
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }
}
