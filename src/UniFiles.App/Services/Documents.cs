using System.Globalization;
using Avalonia.Platform;

namespace UniFiles.App.Services;

/// <summary>
/// Texts shipped inside the app and shown in a window over the page: third-party notices and the
/// release notes of each version (build/release-notes, prereleases left out): "0.3.0.md" in French,
/// "0.3.0.en.md" in English, picked from the interface language (French when one is missing).
/// </summary>
public static class Documents
{
    private const string Root = "avares://UniFiles/Assets/Docs/";

    public static string ThirdPartyNotices() => Read(new Uri(Root + "THIRD-PARTY-NOTICES.md")) ?? "";

    /// <summary>Notes of every version in the interface language, newest first.</summary>
    public static IReadOnlyList<(Version Version, string Markdown)> ReleaseNotes()
    {
        var english = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "fr";
        var notes = new Dictionary<Version, (string Text, bool English)>();
        foreach (var uri in AssetLoader.GetAssets(new Uri(Root + "release-notes/"), null))
        {
            var name = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
            var isEnglish = name.EndsWith(".en", StringComparison.Ordinal);
            if (isEnglish)
                name = name[..^3];
            if (!Version.TryParse(name, out var version) || Read(uri) is not { } text)
                continue;
            // The interface language wins; the other one only fills a gap.
            if (!notes.TryGetValue(version, out var known) || (known.English != english && isEnglish == english))
                notes[version] = (text, isEnglish);
        }
        return notes.OrderByDescending(n => n.Key).Select(n => (n.Key, n.Value.Text)).ToList();
    }

    /// <summary>All the notes in one text, a rule between versions.</summary>
    public static string AllReleaseNotes() => string.Join("\n\n---\n\n", ReleaseNotes().Select(n => n.Markdown.Trim()));

    /// <summary>One version's notes, without their "# UniFiles x.y.z" title (the window has its own).</summary>
    public static string? ReleaseNotesFor(Version version) =>
        ReleaseNotes().FirstOrDefault(n => n.Version.ToString(3) == version.ToString(3)).Markdown is { } notes
            ? string.Join('\n', notes.Replace("\r\n", "\n").Split('\n').SkipWhile(l => l.StartsWith("# ") || l.Trim().Length == 0))
            : null;

    private static string? Read(Uri uri)
    {
        try
        {
            using var reader = new StreamReader(AssetLoader.Open(uri));
            return reader.ReadToEnd();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
