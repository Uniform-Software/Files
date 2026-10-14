using NetVips;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UniFiles.Core.Tools;
using UniFiles.Core.Tools.Images;
using UniFiles.Core.Tools.Pdf;

namespace UniFiles.Core.Tests;

public class ImageToolTests
{
    /// <summary>A noisy photo-like JPEG with EXIF data, so compression has real work to do.</summary>
    private static string MakePhoto(TempFolder temp, string name, int width = 1200, int height = 800)
    {
        var path = temp.File(name);
        using var noise = Image.Gaussnoise(width, height, mean: 128, sigma: 40);
        using var rgb = noise.Bandjoin(noise, noise).Cast(Enums.BandFormat.Uchar).Copy(interpretation: Enums.Interpretation.Srgb);
        using var tagged = rgb.Mutate(m => m.Set(GValue.GStrType, "exif-ifd0-Make", "TestCamera"));
        tagged.WriteToFile(path, new VOption { { "Q", 98 } });
        return path;
    }

    private static Task<IReadOnlyList<string>> Run(IFileTool tool, string input, string output, Action<ToolSettings>? configure = null)
    {
        var settings = ToolSettings.FromDefaults(tool);
        configure?.Invoke(settings);
        return tool.ExecuteAsync(new ToolContext { Inputs = [input], Settings = settings, Output = new OutputFolder(output) }, CancellationToken.None);
    }

    [Fact]
    public async Task Compress_makes_a_smaller_file_and_keeps_the_original()
    {
        using var temp = new TempFolder();
        var input = MakePhoto(temp, "photo.jpg");
        var hash = TempFolder.Hash(input);

        var output = (await Run(new ImageCompressTool(), input, temp.Sub("out"))).Single();

        Assert.True(new FileInfo(output).Length < new FileInfo(input).Length);
        Assert.Equal(hash, TempFolder.Hash(input));
        using var image = Image.NewFromFile(output);
        Assert.Equal(1200, image.Width);
        // Metadata removed by default.
        Assert.DoesNotContain("exif-data", image.GetFields());
    }

    [Theory]
    [InlineData(3000)]
    [InlineData(40)]
    public async Task Compress_to_a_target_size_stays_under_it(int targetKb)
    {
        using var temp = new TempFolder();
        var input = MakePhoto(temp, "photo.jpg", 2400, 1600);

        var output = (await Run(new ImageCompressTool(), input, temp.Sub("out"), s =>
        {
            s["mode"] = "size";
            s["targetKb"] = targetKb;
        })).Single();

        Assert.InRange(new FileInfo(output).Length, 1, targetKb * 1024L);
        using var image = Image.NewFromFile(output);
        // Noise compresses badly: the small target needs smaller dimensions, the large one doesn't.
        Assert.Equal(targetKb >= 3000, image.Width == 2400);
    }

    [Fact]
    public async Task Heic_photos_become_jpg_and_only_heic_is_accepted()
    {
        // HEIC pixels are decoded by ffmpeg: passes trivially on machines without it.
        if (ExternalTools.FindFfmpeg() is null)
            return;
        using var temp = new TempFolder();
        var input = Path.Combine(AppContext.BaseDirectory, "Fixtures", "photo.heic");
        var tool = new ImageFromHeicTool();

        var output = (await Run(tool, input, temp.Sub("out"))).Single();

        Assert.Equal(".jpg", Path.GetExtension(output));
        using var image = Image.NewFromFile(output);
        Assert.Equal((64, 48), (image.Width, image.Height));
        Assert.True(tool.Accepts("IMG_0001.HEIC"));
        Assert.False(tool.Accepts("photo.jpg"));
        Assert.Contains(tool.Id, new ToolCatalog().CompatibleWith([input]).Select(t => t.Id));
        Assert.DoesNotContain(tool.Id, new ToolCatalog().CompatibleWith(["photo.jpg"]).Select(t => t.Id));
    }

    [Fact]
    public async Task Resize_fits_inside_the_box_without_enlarging()
    {
        using var temp = new TempFolder();
        var input = MakePhoto(temp, "wide.jpg", 2000, 1000);
        var outDir = temp.Sub("out");

        var shrunk = (await Run(new ImageResizeTool(), input, outDir, s => { s["maxWidth"] = 500; s["maxHeight"] = 500; })).Single();
        var untouched = (await Run(new ImageResizeTool(), input, outDir, s => { s["maxWidth"] = 5000; s["maxHeight"] = 0; })).Single();

        using (var image = Image.NewFromFile(shrunk))
            Assert.Equal((500, 250), (image.Width, image.Height));
        using (var image = Image.NewFromFile(untouched))
            Assert.Equal((2000, 1000), (image.Width, image.Height));
    }

    [Theory]
    [InlineData("webp")]
    [InlineData("png")]
    [InlineData("avif")]
    [InlineData("jpg")]
    public async Task Convert_writes_the_chosen_format(string format)
    {
        using var temp = new TempFolder();
        var input = MakePhoto(temp, "photo.jpg", 300, 200);

        var output = (await Run(new ImageConvertTool(), input, temp.Sub("out"), s => s["format"] = format)).Single();

        Assert.Equal("." + format, Path.GetExtension(output));
        using var image = Image.NewFromFile(output);
        Assert.Equal(300, image.Width);
    }

    [Fact]
    public async Task A_broken_image_gives_a_readable_error_and_no_leftover()
    {
        using var temp = new TempFolder();
        var input = temp.File("broken.jpg");
        File.WriteAllText(input, "not an image");
        var outDir = temp.Sub("out");

        var error = await Assert.ThrowsAsync<ToolException>(() => Run(new ImageCompressTool(), input, outDir));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Empty(Directory.GetFiles(outDir));
    }
}

public class PdfToolTests
{
    private static string MakePdf(TempFolder temp, string name, int pages)
    {
        var path = temp.File(name);
        using var document = new PdfDocument();
        for (var i = 0; i < pages; i++)
        {
            var page = document.AddPage();
            using var graphics = XGraphics.FromPdfPage(page);
            graphics.DrawRectangle(XBrushes.Coral, 50, 50, 100 + i * 10, 100);
        }
        document.Save(path);
        return path;
    }

    private static int PageCount(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return document.PageCount;
    }

    [Fact]
    public async Task Organize_reorders_rotates_and_drops_pages()
    {
        using var temp = new TempFolder();
        var input = MakePdf(temp, "doc.pdf", 4);
        var tool = new PdfOrganizeTool();
        var settings = ToolSettings.FromDefaults(tool);
        settings[PdfOrganizeTool.PagesKey] = PdfOrganizeTool.FormatPages([new(3, 0), new(0, 90), new(2, 270)]);

        var output = (await tool.ExecuteAsync(new ToolContext
        {
            Inputs = [input],
            Settings = settings,
            Output = new OutputFolder(temp.Sub("out")),
        }, CancellationToken.None)).Single();

        using var document = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal(3, document.PageCount);
        Assert.Equal([0, 90, 270], document.Pages.Cast<PdfPage>().Select(p => p.Rotate));
        Assert.Throws<ToolException>(() => PdfOrganizeTool.ParsePages(""));
    }

    [Fact]
    public void Renderer_draws_pages_on_white()
    {
        using var temp = new TempFolder();
        var input = MakePdf(temp, "doc.pdf", 2);

        using var renderer = PdfRenderer.Open(input);
        var pixels = renderer.Render(1, 200, out var width, out var height);

        Assert.Equal(2, renderer.PageCount);
        // PDFsharp's default page is Letter portrait (612 x 792 pt): 200 px tall.
        Assert.Equal((155, 200), (width, height));
        Assert.Equal(width * height * 4, pixels.Length);
        // Top-left corner is white paper (BGRA), the coral rectangle starts further in.
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, pixels[..4]);
        var inside = (25 * width + 25) * 4; // about (100, 100) pt, inside the rectangle
        Assert.Equal((0x50, 0x7F, 0xFF), (pixels[inside], pixels[inside + 1], pixels[inside + 2]));
    }

    [Fact]
    public void Renderer_rejects_a_damaged_file()
    {
        using var temp = new TempFolder();
        var path = temp.File("broken.pdf");
        File.WriteAllText(path, "%PDF-1.4 not really");
        Assert.Throws<ToolException>(() => PdfRenderer.Open(path));
    }

    [Fact]
    public async Task Merge_combines_all_pages_in_order()
    {
        using var temp = new TempFolder();
        var inputs = new[] { MakePdf(temp, "b.pdf", 2), MakePdf(temp, "a.pdf", 3) };
        var tool = new PdfMergeTool();

        var output = (await tool.ExecuteAsync(new ToolContext
        {
            Inputs = inputs,
            Settings = ToolSettings.FromDefaults(tool),
            Output = new OutputFolder(temp.Sub("out")),
        }, CancellationToken.None)).Single();

        Assert.Equal(5, PageCount(output));
        Assert.StartsWith("b", Path.GetFileName(output));
    }

    [Fact]
    public async Task Split_by_ranges_writes_one_file_per_range()
    {
        using var temp = new TempFolder();
        var input = MakePdf(temp, "doc.pdf", 6);
        var tool = new PdfSplitTool();
        var settings = ToolSettings.FromDefaults(tool);
        settings["mode"] = "ranges";
        settings["ranges"] = "1-2, 3, 4-6";

        var outputs = await tool.ExecuteAsync(new ToolContext
        {
            Inputs = [input],
            Settings = settings,
            Output = new OutputFolder(temp.Sub("out")),
        }, CancellationToken.None);

        Assert.Equal([2, 1, 3], outputs.Select(PageCount));
    }

    [Fact]
    public async Task Images_to_pdf_makes_one_page_per_image()
    {
        using var temp = new TempFolder();
        var images = new List<string>();
        foreach (var (name, width, height) in new[] { ("tall.png", 200, 400), ("wide.webp", 400, 200) })
        {
            var path = temp.File(name);
            using var black = Image.Black(width, height, bands: 3);
            using var image = (black + new double[] { 200, 100, 50 }).Cast(Enums.BandFormat.Uchar);
            image.WriteToFile(path);
            images.Add(path);
        }
        var tool = new ImagesToPdfTool();

        var output = (await tool.ExecuteAsync(new ToolContext
        {
            Inputs = images,
            Settings = ToolSettings.FromDefaults(tool),
            Output = new OutputFolder(temp.Sub("out")),
        }, CancellationToken.None)).Single();

        using var document = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal(2, document.PageCount);
        // A4 turned to match each picture.
        Assert.True(document.Pages[0].Height.Point > document.Pages[0].Width.Point);
        Assert.True(document.Pages[1].Width.Point > document.Pages[1].Height.Point);
    }

    [Fact]
    public async Task Compress_shrinks_a_pdf_with_photos_when_ghostscript_is_installed()
    {
        // Ghostscript is an external program: nothing to check on machines without it.
        if (ExternalTools.FindGhostscript() is null)
            return;

        using var temp = new TempFolder();
        var photo = temp.File("photo.jpg");
        using (var noise = Image.Gaussnoise(2400, 1600, mean: 128, sigma: 40))
        using (var rgb = noise.Bandjoin(noise, noise).Cast(Enums.BandFormat.Uchar).Copy(interpretation: Enums.Interpretation.Srgb))
            rgb.WriteToFile(photo, new VOption { { "Q", 95 } });
        var toPdf = new ImagesToPdfTool();
        var pdf = (await toPdf.ExecuteAsync(new ToolContext
        {
            Inputs = [photo, photo],
            Settings = ToolSettings.FromDefaults(toPdf),
            Output = new OutputFolder(temp.Sub("pdf")),
        }, CancellationToken.None)).Single();

        var tool = new PdfCompressTool();
        var settings = ToolSettings.FromDefaults(tool);
        settings["level"] = "screen";
        var output = (await tool.ExecuteAsync(new ToolContext
        {
            Inputs = [pdf],
            Settings = settings,
            Output = new OutputFolder(temp.Sub("out")),
        }, CancellationToken.None)).Single();

        Assert.True(new FileInfo(output).Length < new FileInfo(pdf).Length / 2);
        Assert.Equal(2, PageCount(output));
    }
}

public class VideoTrimTests
{
    [Fact]
    public void Segments_round_trip()
    {
        var text = UniFiles.Core.Tools.Video.VideoTrimTool.FormatSegments([new(5, 12.25), new(20, 27.5)]);

        Assert.Equal("5-12.25;20-27.5", text);
        Assert.Equal([new(5, 12.25), new(20, 27.5)], UniFiles.Core.Tools.Video.VideoTrimTool.ParseSegments(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("5-3")]
    [InlineData("a-b")]
    public void Bad_segments_are_rejected(string text) =>
        Assert.Throws<ToolException>(() => UniFiles.Core.Tools.Video.VideoTrimTool.ParseSegments(text));

    [Fact]
    public async Task Keeps_and_joins_several_passages_when_ffmpeg_is_installed()
    {
        if (ExternalTools.FindFfmpeg() is not { } ffmpeg)
            return;

        using var temp = new TempFolder();
        var clip = temp.File("clip.mp4");
        var (exitCode, errors) = await ExternalTools.RunAsync(ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25:duration=10",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=10",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", clip,
        ], CancellationToken.None);
        Assert.True(exitCode == 0, errors);

        var tool = new UniFiles.Core.Tools.Video.VideoTrimTool();
        var settings = ToolSettings.FromDefaults(tool);
        settings[UniFiles.Core.Tools.Video.VideoTrimTool.SegmentsKey] = "1-3;6-9";

        var output = (await tool.ExecuteAsync(new ToolContext
        {
            Inputs = [clip],
            Settings = settings,
            Output = new OutputFolder(temp.Sub("out")),
        }, CancellationToken.None)).Single();

        var info = await UniFiles.Core.Tools.Video.VideoThumbnails.ProbeAsync(output, CancellationToken.None);
        Assert.InRange(info.Duration.TotalSeconds, 4.9, 5.2);
        Assert.True(info.HasAudio);
    }

    [Fact]
    public async Task Exports_kept_passages_as_an_animated_gif()
    {
        if (ExternalTools.FindFfmpeg() is not { } ffmpeg)
            return;

        using var temp = new TempFolder();
        var clip = temp.File("clip.mp4");
        var (exitCode, errors) = await ExternalTools.RunAsync(ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=6",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", clip,
        ], CancellationToken.None);
        Assert.True(exitCode == 0, errors);

        var tool = new UniFiles.Core.Tools.Video.VideoTrimTool();
        var settings = ToolSettings.FromDefaults(tool);
        settings[UniFiles.Core.Tools.Video.VideoTrimTool.SegmentsKey] = "0-1;4-5";
        settings["output"] = "gif";
        settings["gifWidth"] = "320";
        settings["gifFps"] = "10";

        var output = (await tool.ExecuteAsync(new ToolContext
        {
            Inputs = [clip],
            Settings = settings,
            Output = new OutputFolder(temp.Sub("out")),
        }, CancellationToken.None)).Single();

        Assert.Equal(".gif", Path.GetExtension(output));
        // libvips reads every frame of a GIF as a tall strip: 2 s at 10 fps = 20 frames of 320x180.
        using var gif = NetVips.Image.NewFromFile(output, kwargs: new NetVips.VOption { { "n", -1 } });
        Assert.Equal(320, gif.Width);
        Assert.Equal(20, gif.Height / gif.PageHeight);
        Assert.Equal(180, gif.PageHeight);
    }
}

