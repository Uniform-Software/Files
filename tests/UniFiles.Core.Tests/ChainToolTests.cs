using NetVips;
using PdfSharp.Pdf;
using UniFiles.Core.Jobs;
using UniFiles.Core.Storage;
using UniFiles.Core.Tools;
using UniFiles.Core.Tools.Files;
using UniFiles.Core.Tools.Images;
using UniFiles.Core.Tools.Pdf;
using UniFiles.Core.Tools.Video;

namespace UniFiles.Core.Tests;

public class ChainToolTests
{
    private static string MakePhoto(TempFolder temp, string name)
    {
        var path = temp.File(name);
        using var noise = Image.Gaussnoise(1200, 800, mean: 128, sigma: 30);
        using var rgb = noise.Bandjoin(noise, noise).Cast(Enums.BandFormat.Uchar).Copy(interpretation: Enums.Interpretation.Srgb);
        using var tagged = rgb.Mutate(m => m.Set(GValue.GStrType, "exif-ifd0-Make", "TestCamera"));
        tagged.WriteToFile(path, new VOption { { "Q", 95 } });
        return path;
    }

    private static ChainStep Step(IFileTool tool, Action<ToolSettings>? configure = null)
    {
        var settings = ToolSettings.FromDefaults(tool);
        configure?.Invoke(settings);
        return new ChainStep(tool, settings);
    }

    private static int LeftoverTempFolders() =>
        Directory.EnumerateDirectories(Path.GetTempPath(), "unifiles-chain-*").Count();

    [Fact]
    public async Task Resize_convert_and_strip_give_one_webp_per_photo()
    {
        using var temp = new TempFolder();
        var input = MakePhoto(temp, "photo.jpg");
        var before = LeftoverTempFolders();
        var chain = new ChainTool([
            Step(new ImageResizeTool(), s => { s["maxWidth"] = 600; s["maxHeight"] = 600; }),
            Step(new ImageConvertTool(), s => s["format"] = "webp"),
            Step(new ImageStripMetadataTool()),
        ]);

        var job = new JobQueue().Enqueue(chain, [InputFile.Of(input)], new ToolSettings(), OutputTarget.Fixed(temp.Sub("out")));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(2, chain.Name.Count(c => c == '→'));
        var output = Assert.Single(Directory.GetFiles(temp.Sub("out")));
        Assert.Equal(".webp", Path.GetExtension(output));
        using var image = Image.NewFromFile(output);
        Assert.Equal((600, 400), (image.Width, image.Height));
        Assert.DoesNotContain("exif-data", image.GetFields());
        Assert.Equal(before, LeftoverTempFolders());
    }

    [Fact]
    public async Task Pdf_to_images_then_compress_multiplies_the_files()
    {
        using var temp = new TempFolder();
        var pdf = temp.File("doc.pdf");
        using (var document = new PdfDocument())
        {
            for (var i = 0; i < 3; i++)
                document.AddPage();
            document.Save(pdf);
        }
        var chain = new ChainTool([
            Step(new PdfToImagesTool(), s => { s["dpi"] = "72"; s["format"] = "jpg"; }),
            Step(new ImageCompressTool()),
        ]);

        var outputs = await chain.ExecuteAsync(new ToolContext
        {
            Inputs = [pdf],
            Settings = new ToolSettings(),
            Output = new OutputFolder(temp.Sub("out")),
        }, CancellationToken.None);

        Assert.Equal(3, outputs.Count);
        Assert.All(outputs, o => Assert.Equal(".jpg", Path.GetExtension(o)));
        Assert.All(outputs, o => Assert.StartsWith(temp.Sub("out"), o));
    }

    [Fact]
    public async Task A_combining_last_step_gets_every_result()
    {
        using var temp = new TempFolder();
        var inputs = new[] { MakePhoto(temp, "a.jpg"), MakePhoto(temp, "b.jpg") };
        var chain = new ChainTool([Step(new ImageCompressTool()), Step(new ZipTool())]);
        Assert.True(chain.CombinesInputs);

        var job = new JobQueue().Enqueue(chain, inputs.Select(InputFile.Of).ToList(), new ToolSettings(), OutputTarget.Fixed(temp.Sub("out")));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(20));

        var zip = Assert.Single(Directory.GetFiles(temp.Sub("out")));
        using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
        Assert.Equal(["a.jpg", "b.jpg"], archive.Entries.Select(e => e.Name).Order());
    }

    [Fact]
    public void Validation_refuses_impossible_chains()
    {
        Assert.Null(ChainTool.Validate([new ImageResizeTool(), new ImageConvertTool()]));
        Assert.Null(ChainTool.Validate([new PdfToImagesTool(), new ImageCompressTool()], FileKind.Pdf));

        Assert.NotNull(ChainTool.Validate([new ImageResizeTool()]));                           // one step
        Assert.NotNull(ChainTool.Validate([new VideoTrimTool(), new VideoCompressTool()]));      // editor
        Assert.NotNull(ChainTool.Validate([new ZipTool(), new ImageCompressTool()]));            // combining, not last
        Assert.NotNull(ChainTool.Validate([new ImagesToPdfTool(), new PdfCompressTool()]));      // combining, not last
        Assert.NotNull(ChainTool.Validate([new ImageResizeTool(), new PdfSplitTool()]));         // images → PDF tool
        Assert.NotNull(ChainTool.Validate([new AudioExtractTool(), new VideoMuteTool()], FileKind.Video)); // audio → video tool
    }

    [Fact]
    public async Task A_failing_step_names_itself_and_cleans_up()
    {
        using var temp = new TempFolder();
        var broken = temp.File("broken.jpg");
        File.WriteAllText(broken, "not an image");
        var before = LeftoverTempFolders();
        var chain = new ChainTool([Step(new ImageResizeTool()), Step(new ImageConvertTool())]);

        var error = await Assert.ThrowsAsync<ToolException>(() => chain.ExecuteAsync(new ToolContext
        {
            Inputs = [broken],
            Settings = new ToolSettings(),
            Output = new OutputFolder(temp.Sub("out")),
        }, CancellationToken.None));

        Assert.Contains("1", error.Message);
        Assert.Equal(before, LeftoverTempFolders());
    }

    [Fact]
    public void A_chain_preset_resolves_with_its_steps_and_destination()
    {
        var preset = new ToolPreset
        {
            Name = "Web",
            ToolId = ChainTool.ChainId,
            Values = new() { [OutputSettings.ModeKey] = System.Text.Json.JsonSerializer.SerializeToElement("DatedSubfolder") },
            Steps =
            [
                new PresetStep { ToolId = "image.resize", Values = new() { ["maxWidth"] = System.Text.Json.JsonSerializer.SerializeToElement(1600) } },
                new PresetStep { ToolId = "image.convert", Values = new() { ["format"] = System.Text.Json.JsonSerializer.SerializeToElement("webp") } },
            ],
        };

        var (tool, settings) = PresetResolver.Resolve(new ToolCatalog(), preset)!.Value;

        var chain = Assert.IsType<ChainTool>(tool);
        Assert.Equal(1600, chain.Steps[0].Settings.GetInt("maxWidth"));
        Assert.Equal("webp", chain.Steps[1].Settings.GetString("format"));
        Assert.Equal(OutputMode.DatedSubfolder, OutputSettings.Read(settings, "/out").Mode);
    }
}
