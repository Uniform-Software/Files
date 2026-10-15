using System.Globalization;
using System.IO.Compression;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UniFiles.Core.Tools;
using UniFiles.Core.Tools.Files;
using UniFiles.Core.Tools.Pdf;
using UniFiles.Core.Tools.Video;

namespace UniFiles.Core.Tests;

/// <summary>Actions added in 0.3: PDF to images, PDF metadata, normalize, mute, ZIP.</summary>
public class NewToolTests
{
    private static async Task<IReadOnlyList<string>> Run(IFileTool tool, IReadOnlyList<InputFile> inputs, string output, Action<ToolSettings>? configure = null)
    {
        var settings = ToolSettings.FromDefaults(tool);
        configure?.Invoke(settings);
        return await tool.ExecuteAsync(new ToolContext
        {
            Inputs = inputs.Select(i => i.Path).ToList(),
            Files = inputs,
            Settings = settings,
            Output = new OutputFolder(output),
        }, CancellationToken.None);
    }

    private static string MakePdf(TempFolder temp, int pages, string? author = null)
    {
        var path = temp.File("doc.pdf");
        using var document = new PdfDocument();
        for (var i = 0; i < pages; i++)
            document.AddPage();
        if (author is not null)
        {
            document.Info.Author = author;
            document.Info.Title = "Rapport";
        }
        document.Save(path);
        return path;
    }

    [Fact]
    public async Task Pdf_to_images_writes_one_image_per_page_at_the_chosen_resolution()
    {
        using var temp = new TempFolder();
        var input = MakePdf(temp, 3);

        var outputs = await Run(new PdfToImagesTool(), [InputFile.Of(input)], temp.Sub("out"), s => s["dpi"] = "72");

        Assert.Equal(3, outputs.Count);
        // Several pages go in a folder named after the PDF, numbered on 2 digits.
        Assert.All(outputs, o => Assert.Equal("doc", Path.GetFileName(Path.GetDirectoryName(o))));
        Assert.EndsWith("doc - page 01.png", outputs[0]);
        using var image = NetVips.Image.NewFromFile(outputs[0]);
        // PDFsharp's default page is Letter: 612 x 792 pt, i.e. pixels at 72 dpi.
        Assert.Equal((612, 792), (image.Width, image.Height));
    }

    [Fact]
    public async Task Pdf_to_images_can_take_some_pages_only()
    {
        using var temp = new TempFolder();
        var input = MakePdf(temp, 5);

        var outputs = await Run(new PdfToImagesTool(), [InputFile.Of(input)], temp.Sub("out"), s =>
        {
            s["pages"] = "ranges";
            s["ranges"] = "2, 4";
            s["format"] = "jpg";
            s["dpi"] = "72";
        });

        Assert.Equal(2, outputs.Count);
        Assert.All(outputs, o => Assert.EndsWith(".jpg", o));
    }

    [Fact]
    public async Task Pdf_metadata_are_removed()
    {
        using var temp = new TempFolder();
        var input = MakePdf(temp, 1, author: "Jean Secret");

        var output = (await Run(new PdfStripMetadataTool(), [InputFile.Of(input)], temp.Sub("out"))).Single();

        Assert.DoesNotContain("Jean Secret", File.ReadAllText(output, System.Text.Encoding.Latin1));
        using var document = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal("", document.Info.Author);
        Assert.Equal("", document.Info.Title);
    }

    [Fact]
    public async Task Zip_keeps_the_dropped_folder_tree_and_accents()
    {
        using var temp = new TempFolder();
        var root = temp.Sub("Été");
        Directory.CreateDirectory(Path.Combine(root, "Plage"));
        File.WriteAllText(Path.Combine(root, "à lire.txt"), "a");
        File.WriteAllText(Path.Combine(root, "Plage", "photo.txt"), "b");
        var inputs = FileKindDetector.ExpandInputs([root]);

        var output = (await Run(new ZipTool(), inputs, temp.Sub("out"))).Single();

        Assert.Equal("Été.zip", Path.GetFileName(output));
        using var archive = ZipFile.OpenRead(output);
        Assert.Equal(["Plage/photo.txt", "à lire.txt"], archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Zip_never_writes_two_entries_with_the_same_name()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("a.txt", ZipTool.EntryName(InputFile.Of("/x/a.txt"), used));
        Assert.Equal("a (2).txt", ZipTool.EntryName(InputFile.Of("/y/a.txt"), used));
    }

    private static async Task<string?> MakeClip(TempFolder temp, string name, string audio, bool video)
    {
        if (ExternalTools.FindFfmpeg() is not { } ffmpeg)
            return null;
        var clip = temp.File(name);
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        if (video)
            arguments.AddRange(["-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25:duration=6"]);
        arguments.AddRange(["-f", "lavfi", "-i", audio]);
        if (video)
            arguments.AddRange(["-c:v", "libx264", "-pix_fmt", "yuv420p"]);
        arguments.AddRange(["-c:a", video ? "aac" : "pcm_s16le", "-shortest", clip]);
        var (exitCode, errors) = await ExternalTools.RunAsync(ffmpeg, arguments, CancellationToken.None);
        Assert.True(exitCode == 0, errors);
        return clip;
    }

    /// <summary>Integrated loudness measured by ffmpeg's ebur128 filter.</summary>
    private static async Task<double> Loudness(string file)
    {
        var (_, log) = await ExternalTools.RunAsync(ExternalTools.FindFfmpeg()!,
            ["-hide_banner", "-nostats", "-i", file, "-af", "ebur128", "-f", "null", "-"], CancellationToken.None);
        var line = log.Split('\n').Last(l => l.TrimStart().StartsWith("I:"));
        return double.Parse(line.Split(':')[1].Replace("LUFS", "").Trim(), CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Normalize_brings_quiet_sound_to_the_target()
    {
        using var temp = new TempFolder();
        // Pink noise at a low volume: about −35 LUFS.
        if (await MakeClip(temp, "quiet.wav", "anoisesrc=color=pink:amplitude=0.02:duration=6", video: false) is not { } clip)
            return;

        var output = (await Run(new AudioNormalizeTool(), [InputFile.Of(clip)], temp.Sub("out"), s => s["target"] = "-16")).Single();

        Assert.Equal(".wav", Path.GetExtension(output));
        Assert.InRange(await Loudness(output), -17.5, -14.5);
    }

    [Fact]
    public async Task Normalize_keeps_the_video_and_refuses_silence()
    {
        using var temp = new TempFolder();
        if (await MakeClip(temp, "clip.mp4", "anoisesrc=color=pink:amplitude=0.02:duration=6", video: true) is not { } clip)
            return;

        var output = (await Run(new AudioNormalizeTool(), [InputFile.Of(clip)], temp.Sub("out"))).Single();
        var info = await VideoThumbnails.ProbeAsync(output, CancellationToken.None);
        Assert.Equal((320, 240), (info.Width, info.Height));
        Assert.True(info.HasAudio);

        Assert.Null(AudioNormalizeTool.Measures("no json here"));
        Assert.Null(AudioNormalizeTool.Measures("""{ "input_i" : "-inf", "input_tp" : "-inf", "input_lra" : "0.00", "input_thresh" : "-inf", "target_offset" : "inf" }"""));
    }

    [Fact]
    public async Task Mute_removes_the_sound_and_keeps_the_picture()
    {
        using var temp = new TempFolder();
        if (await MakeClip(temp, "clip.mp4", "sine=frequency=440:duration=6", video: true) is not { } clip)
            return;

        var output = (await Run(new VideoMuteTool(), [InputFile.Of(clip)], temp.Sub("out"))).Single();

        var info = await VideoThumbnails.ProbeAsync(output, CancellationToken.None);
        Assert.False(info.HasAudio);
        Assert.Equal((320, 240), (info.Width, info.Height));
    }
}
