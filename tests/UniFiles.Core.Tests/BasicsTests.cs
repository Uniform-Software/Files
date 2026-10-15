using UniFiles.Core.Storage;
using UniFiles.Core.Tools;
using UniFiles.Core.Tools.Pdf;
using UniFiles.Core.Tools.Rename;
using UniFiles.Core.Tools.Video;

namespace UniFiles.Core.Tests;

public class OutputFolderTests
{
    [Fact]
    public void Reserve_never_reuses_an_existing_name()
    {
        using var temp = new TempFolder();
        var output = new OutputFolder(temp.Path);
        File.WriteAllText(temp.File("photo.jpg"), "existing");

        var first = output.Reserve("photo.jpg");
        var second = output.Reserve("photo.jpg");

        Assert.Equal(temp.File("photo (2).jpg"), first);
        Assert.Equal(temp.File("photo (3).jpg"), second);
        Assert.Equal("existing", File.ReadAllText(temp.File("photo.jpg")));
    }

    [Fact]
    public void Reserve_is_safe_in_parallel()
    {
        using var temp = new TempFolder();
        var output = new OutputFolder(temp.Path);

        var paths = Enumerable.Range(0, 50).AsParallel().Select(_ => output.Reserve("same.txt")).ToList();

        Assert.Equal(50, paths.Distinct().Count());
    }

    [Theory]
    [InlineData("a:b?.txt", "a_b_.txt")]
    [InlineData("  name. ", "name")]
    [InlineData(".png", "file.png")]
    public void Sanitize_produces_valid_names(string input, string expected) =>
        Assert.Equal(expected, OutputFolder.Sanitize(input));
}

public class FileKindTests
{
    [Theory]
    [InlineData("a.JPG", FileKind.Image)]
    [InlineData("a.heic", FileKind.Image)]
    [InlineData("a.pdf", FileKind.Pdf)]
    [InlineData("a.mkv", FileKind.Video)]
    [InlineData("a.flac", FileKind.Audio)]
    [InlineData("a.docx", FileKind.Unknown)]
    public void Detect_uses_the_extension(string path, FileKind expected) =>
        Assert.Equal(expected, FileKindDetector.Detect(path));

    [Fact]
    public void ExpandPaths_walks_folders_and_skips_duplicates()
    {
        using var temp = new TempFolder();
        var sub = temp.Sub("sub");
        File.WriteAllText(temp.File("a.jpg"), "");
        File.WriteAllText(Path.Combine(sub, "b.pdf"), "");

        var files = FileKindDetector.ExpandPaths([temp.Path, temp.File("a.jpg"), temp.File("missing.png")]);

        Assert.Equal(2, files.Count);
    }

    [Fact]
    public void Catalog_offers_only_tools_accepting_every_file()
    {
        var catalog = new ToolCatalog();

        var forImages = catalog.CompatibleWith(["a.jpg", "b.png"]).Select(t => t.Id).ToList();
        var forOnePdf = catalog.CompatibleWith(["a.pdf"]).Select(t => t.Id).ToList();
        var forMixed = catalog.CompatibleWith(["a.jpg", "b.pdf"]).Select(t => t.Id).ToList();

        Assert.Contains("image.compress", forImages);
        Assert.Contains("pdf.fromImages", forImages);
        Assert.DoesNotContain("pdf.merge", forImages);
        Assert.Contains("pdf.split", forOnePdf);
        Assert.DoesNotContain("pdf.merge", forOnePdf); // merging needs two files
        // Only the actions working on any file: renaming and archiving.
        Assert.Equal(["rename.pattern", "files.zip"], forMixed);
    }

    [Fact]
    public void Catalog_ids_are_unique()
    {
        var ids = new ToolCatalog().Tools.Select(t => t.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}

public class RenameTests
{
    [Fact]
    public void NewName_replaces_tokens()
    {
        var settings = new ToolSettings(new Dictionary<string, object?>
        {
            ["pattern"] = "{name}-{n}.{ext}",
            ["start"] = 7,
            ["digits"] = 3,
            ["case"] = "lower",
        });

        Assert.Equal("holiday-008.jpg.jpg", BatchRenameTool.NewName("C:/x/Holiday.JPG", 1, settings));
    }

    [Fact]
    public async Task Execute_copies_and_leaves_the_original_untouched()
    {
        using var temp = new TempFolder();
        var input = temp.File("report.txt");
        File.WriteAllText(input, "content");
        var hash = TempFolder.Hash(input);
        var tool = new BatchRenameTool();
        var settings = ToolSettings.FromDefaults(tool);
        settings["pattern"] = "doc_{n}";

        var outputs = await tool.ExecuteAsync(new ToolContext
        {
            Inputs = [input],
            Index = 0,
            Settings = settings,
            Output = new OutputFolder(temp.Sub("out")),
        }, CancellationToken.None);

        Assert.Equal("doc_001.txt", Path.GetFileName(outputs.Single()));
        Assert.Equal("content", File.ReadAllText(outputs.Single()));
        Assert.Equal(hash, TempFolder.Hash(input));
    }
}

public class ParsingTests
{
    [Fact]
    public void ParseRanges_reads_lists_and_ranges() =>
        Assert.Equal([(1, 3), (5, 5), (8, 10)], PdfSplitTool.ParseRanges("1-3, 5; 8 - 10", 10));

    [Theory]
    [InlineData("3-1")]
    [InlineData("0")]
    [InlineData("a-b")]
    [InlineData("")]
    [InlineData("4-12")]
    public void ParseRanges_rejects_bad_input(string text) =>
        Assert.Throws<ToolException>(() => PdfSplitTool.ParseRanges(text, 10));

    [Theory]
    [InlineData("90", 90)]
    [InlineData("1:30", 90)]
    [InlineData("01:02:03", 3723)]
    [InlineData("0:01,5", 1.5)]
    [InlineData("", 0)]
    public void ParseTime_accepts_common_forms(string text, double seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), VideoToolBase.ParseTime(text));

    [Fact]
    public void ParseTime_rejects_garbage() =>
        Assert.Throws<ToolException>(() => VideoToolBase.ParseTime("soon"));
}

public class StorageTests
{
    [Fact]
    public void Settings_round_trip_and_survive_a_damaged_file()
    {
        using var temp = new TempFolder();
        var store = new SettingsStore(temp.File("settings.json"));
        store.Save(new AppSettings { OutputDirectory = "X:/out", Theme = AppTheme.Light, Language = "en" });

        var loaded = store.Load();
        Assert.Equal("X:/out", loaded.OutputDirectory);
        Assert.Equal(AppTheme.Light, loaded.Theme);

        File.WriteAllText(temp.File("settings.json"), "{ not json");
        Assert.Equal(AppTheme.Dark, store.Load().Theme);
    }

    [Fact]
    public void Presets_keep_their_values()
    {
        using var temp = new TempFolder();
        var store = new SettingsStore(temp.File("settings.json"));
        var settings = new AppSettings();
        settings.Presets.Add(new ToolPreset
        {
            Name = "Web 1600 px",
            ToolId = "image.resize",
            Values = new() { ["maxWidth"] = System.Text.Json.JsonSerializer.SerializeToElement(1600) },
        });
        store.Save(settings);

        var preset = Assert.Single(store.Load().Presets);
        Assert.Equal(("Web 1600 px", "image.resize"), (preset.Name, preset.ToolId));
        Assert.Equal(1600, preset.Values["maxWidth"].GetInt32());
        Assert.Equal(settings.Presets[0].Id, preset.Id);
    }
}
