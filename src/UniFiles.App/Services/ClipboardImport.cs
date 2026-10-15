using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using UniFiles.Core.Resources;

namespace UniFiles.App.Services;

/// <summary>
/// "Paste to process": files copied in the Finder or the Explorer, or a copied picture (a
/// screenshot), saved as a PNG in a temporary folder. Pasted pictures older than a week are
/// deleted at startup.
/// </summary>
public static class ClipboardImport
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

    private static string Folder => Path.Combine(Path.GetTempPath(), "UniFiles", Strings.Paste_Folder);

    /// <summary>Local paths of what the clipboard holds (files first, else a picture); empty if nothing usable.</summary>
    public static async Task<IReadOnlyList<string>> ReadAsync(TopLevel top)
    {
        if (top.Clipboard is not { } clipboard)
            return [];

        if (await clipboard.TryGetFilesAsync() is { Length: > 0 } items)
        {
            var paths = items.Select(i => i.TryGetLocalPath()).OfType<string>().ToList();
            if (paths.Count > 0)
                return paths;
        }

        if (await clipboard.TryGetBitmapAsync() is { } bitmap)
        {
            using (bitmap)
            {
                Directory.CreateDirectory(Folder);
                var path = Path.Combine(Folder, string.Format(Strings.Paste_ImageName, DateTime.Now) + ".png");
                bitmap.Save(path);
                return [path];
            }
        }
        return [];
    }

    public static void CleanOld()
    {
        try
        {
            if (!Directory.Exists(Folder))
                return;
            foreach (var file in Directory.EnumerateFiles(Folder))
            {
                if (DateTime.Now - File.GetLastWriteTime(file) > KeepFor)
                    File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The system empties its temporary folder anyway.
        }
    }
}
