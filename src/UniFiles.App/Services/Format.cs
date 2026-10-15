using UniFiles.Core.Resources;

namespace UniFiles.App.Services;

public static class Format
{
    public static string Size(long bytes)
    {
        string[] units = [Strings.Unit_Bytes, Strings.Unit_KB, Strings.Unit_MB, Strings.Unit_GB];
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    /// <summary>"12.4 MB → 3.1 MB (−75 %)", or null when nothing was measured.</summary>
    public static string? Gain(long before, long after)
    {
        if (before <= 0)
            return null;
        var change = (after - before) * 100.0 / before;
        var percent = change < 0 ? $"−{Math.Abs(change):0}" : $"+{change:0}";
        return string.Format(Strings.Format_Gain, Size(before), Size(after), percent);
    }

    public static string Files(int count) =>
        string.Format(count == 1 ? Strings.Format_OneFile : Strings.Format_Files, count);

    /// <summary>A path with the home folder written "~" (as in the Terminal), shorter to read.</summary>
    public static string ShortPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 0 && path.StartsWith(home, StringComparison.Ordinal) ? "~" + path[home.Length..] : path;
    }

    /// <summary>"2 files already optimized, originals kept", or null when there are none.</summary>
    public static string? KeptOriginals(int count) => count switch
    {
        0 => null,
        1 => Strings.Format_KeptOriginalOne,
        _ => string.Format(Strings.Format_KeptOriginals, count),
    };
}
