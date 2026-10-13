namespace UniFiles.Core;

/// <summary>Broad family of a file, used to decide which tools can handle it.</summary>
public enum FileKind
{
    Unknown,
    Image,
    Pdf,
    Video,
    Audio,
}

/// <summary>The four UniFiles modules. Each one has its own color in the UI.</summary>
public enum ToolModule
{
    Images,
    Rename,
    Pdf,
    Video,
}

public static class FileKindDetector
{
    private static readonly Dictionary<string, FileKind> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = FileKind.Image,
        [".jpeg"] = FileKind.Image,
        [".png"] = FileKind.Image,
        [".webp"] = FileKind.Image,
        [".avif"] = FileKind.Image,
        [".heic"] = FileKind.Image,
        [".heif"] = FileKind.Image,
        [".gif"] = FileKind.Image,
        [".tif"] = FileKind.Image,
        [".tiff"] = FileKind.Image,
        [".bmp"] = FileKind.Image,

        [".pdf"] = FileKind.Pdf,

        [".mp4"] = FileKind.Video,
        [".m4v"] = FileKind.Video,
        [".mov"] = FileKind.Video,
        [".mkv"] = FileKind.Video,
        [".webm"] = FileKind.Video,
        [".avi"] = FileKind.Video,
        [".wmv"] = FileKind.Video,
        [".flv"] = FileKind.Video,
        [".mpg"] = FileKind.Video,
        [".mpeg"] = FileKind.Video,

        [".mp3"] = FileKind.Audio,
        [".wav"] = FileKind.Audio,
        [".flac"] = FileKind.Audio,
        [".ogg"] = FileKind.Audio,
        [".opus"] = FileKind.Audio,
        [".m4a"] = FileKind.Audio,
        [".aac"] = FileKind.Audio,
        [".wma"] = FileKind.Audio,
    };

    public static FileKind Detect(string path) =>
        ByExtension.TryGetValue(Path.GetExtension(path), out var kind) ? kind : FileKind.Unknown;

    /// <summary>
    /// Expands dropped paths: folders are walked recursively, hidden and system files are skipped.
    /// </summary>
    public static IReadOnlyList<string> ExpandPaths(IEnumerable<string> paths)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                };
                foreach (var file in Directory.EnumerateFiles(path, "*", options).Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (seen.Add(file))
                        files.Add(file);
                }
            }
            else if (File.Exists(path) && seen.Add(path))
            {
                files.Add(path);
            }
        }

        return files;
    }
}
