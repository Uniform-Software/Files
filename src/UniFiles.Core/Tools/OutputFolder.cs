namespace UniFiles.Core.Tools;

/// <summary>
/// The folder results are written to. Names are reserved atomically so parallel jobs never
/// overwrite each other, and an existing file is never replaced: "photo.jpg" becomes "photo (2).jpg".
/// </summary>
public sealed class OutputFolder
{
    private static readonly char[] InvalidNameChars =
        System.IO.Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).Distinct().ToArray();

    public OutputFolder(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }

    // Every file UniFiles reserved lately, so a watched folder never processes UniFiles' own results.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> Written = new(StringComparer.Ordinal);
    private static readonly TimeSpan WrittenMemory = TimeSpan.FromMinutes(30);

    /// <summary>Whether UniFiles wrote (or is writing) this file in the last half hour.</summary>
    public static bool WasWrittenByUniFiles(string path) =>
        Written.TryGetValue(System.IO.Path.GetFullPath(path), out var at) && DateTime.UtcNow - at < WrittenMemory;

    /// <summary>
    /// Creates an empty file with a free name based on <paramref name="fileName"/> and returns its path.
    /// The tool then overwrites this placeholder with the real content.
    /// </summary>
    public string Reserve(string fileName)
    {
        Directory.CreateDirectory(Path);

        var safeName = Sanitize(fileName);
        var stem = System.IO.Path.GetFileNameWithoutExtension(safeName);
        var extension = System.IO.Path.GetExtension(safeName);

        for (var attempt = 1; ; attempt++)
        {
            var candidate = attempt == 1 ? safeName : $"{stem} ({attempt}){extension}";
            var fullPath = System.IO.Path.Combine(Path, candidate);
            try
            {
                using (new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                Remember(fullPath);
                return fullPath;
            }
            catch (IOException) when (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                // Name taken: try the next one.
            }
        }
    }

    private static void Remember(string path)
    {
        Written[path] = DateTime.UtcNow;
        if (Written.Count > 5000)
        {
            foreach (var (old, at) in Written)
            {
                if (DateTime.UtcNow - at > WrittenMemory)
                    Written.TryRemove(old, out _);
            }
        }
    }

    /// <summary>Deletes a reserved file after a failure, ignoring errors.</summary>
    public static void Release(string reservedPath)
    {
        try
        {
            if (File.Exists(reservedPath))
                File.Delete(reservedPath);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static string Sanitize(string fileName)
    {
        var cleaned = new string(fileName.Select(c => InvalidNameChars.Contains(c) || char.IsControl(c) ? '_' : c).ToArray())
            .Trim()
            .TrimEnd('.');
        return string.IsNullOrWhiteSpace(System.IO.Path.GetFileNameWithoutExtension(cleaned))
            ? "file" + System.IO.Path.GetExtension(cleaned)
            : cleaned;
    }
}
