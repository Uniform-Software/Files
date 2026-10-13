using NetVips;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UniFiles.Core.Resources;

namespace UniFiles.Core.Tools.Pdf;

public abstract class PdfToolBase : FileToolBase
{
    protected static readonly IReadOnlySet<FileKind> PdfOnly = new HashSet<FileKind> { FileKind.Pdf };

    public override ToolModule Module => ToolModule.Pdf;

    public override IReadOnlySet<FileKind> AcceptedKinds => PdfOnly;

    protected static PdfDocument OpenForImport(string path)
    {
        try
        {
            return PdfReader.Open(path, PdfDocumentOpenMode.Import);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ToolException(Strings.Error_PdfUnreadable, ex);
        }
    }

    protected static void Save(PdfDocument document, string path)
    {
        document.Info.Creator = "UniFiles";
        document.Save(path);
    }
}

public sealed class PdfMergeTool : PdfToolBase
{
    public override string Id => "pdf.merge";
    public override string Name => Strings.Tool_PdfMerge_Name;
    public override string Description => Strings.Tool_PdfMerge_Description;
    public override bool CombinesInputs => true;
    public override bool ShowsSizeChange => false;

    public override IReadOnlyList<ToolOption> Options { get; } =
    [
        new ChoiceOption("order", Strings.Option_Order,
        [
            new Choice("dropped", Strings.Choice_OrderDropped),
            new Choice("name", Strings.Choice_OrderName),
        ], "dropped"),
    ];

    public override long? EstimateOutputSize(IReadOnlyList<string> inputs, ToolSettings settings) => TotalSize(inputs);

    public override async Task<IReadOnlyList<string>> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var inputs = context.Settings.GetString("order") == "name"
            ? context.Inputs.OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase).ToList()
            : context.Inputs.ToList();

        var name = OutputName(inputs[0], ".pdf", " - " + Strings.Suffix_Merged);
        var output = await WriteOutputAsync(context.Output, name, path => Task.Run(() =>
        {
            using var merged = new PdfDocument();
            for (var i = 0; i < inputs.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var source = OpenForImport(inputs[i]);
                foreach (var page in source.Pages)
                    merged.AddPage(page);
                context.Progress.Report((i + 1.0) / (inputs.Count + 1));
            }
            Save(merged, path);
        }, cancellationToken));

        context.Progress.Report(1);
        return [output];
    }
}

public sealed class PdfSplitTool : PdfToolBase
{
    public override string Id => "pdf.split";
    public override string Name => Strings.Tool_PdfSplit_Name;
    public override string Description => Strings.Tool_PdfSplit_Description;
    public override bool ShowsSizeChange => false;

    public override IReadOnlyList<ToolOption> Options { get; } =
    [
        new ChoiceOption("mode", Strings.Option_SplitMode,
        [
            new Choice("each", Strings.Choice_SplitEachPage),
            new Choice("ranges", Strings.Choice_SplitRanges),
        ], "each"),
        new TextOption("ranges", Strings.Option_Ranges, "1-3, 4-6")
        {
            Hint = Strings.Hint_Ranges,
            VisibleWhenKey = "mode",
            VisibleWhenValue = "ranges",
        },
    ];

    public override long? EstimateOutputSize(IReadOnlyList<string> inputs, ToolSettings settings) => TotalSize(inputs);

    public override async Task<IReadOnlyList<string>> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var input = context.Inputs[0];
        using var source = OpenForImport(input);
        var pageCount = source.PageCount;

        var ranges = context.Settings.GetString("mode") == "ranges"
            ? ParseRanges(context.Settings.GetString("ranges"), pageCount)
            : Enumerable.Range(1, pageCount).Select(p => (p, p)).ToList();

        var outputs = new List<string>();
        for (var i = 0; i < ranges.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (first, last) = ranges[i];
            var suffix = first == last ? $" - p{first}" : $" - p{first}-{last}";
            outputs.Add(await WriteOutputAsync(context.Output, OutputName(input, ".pdf", suffix), path => Task.Run(() =>
            {
                using var part = new PdfDocument();
                for (var page = first; page <= last; page++)
                    part.AddPage(source.Pages[page - 1]);
                Save(part, path);
            }, cancellationToken)));
            context.Progress.Report((i + 1.0) / ranges.Count);
        }
        return outputs;
    }

    /// <summary>Parses "1-3, 5, 8-10" into page ranges, checking them against the page count.</summary>
    public static List<(int First, int Last)> ParseRanges(string text, int pageCount)
    {
        var ranges = new List<(int, int)>();
        foreach (var part in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = part.Split('-', StringSplitOptions.TrimEntries);
            if (bounds.Length is < 1 or > 2
                || !int.TryParse(bounds[0], out var first)
                || !int.TryParse(bounds[^1], out var last)
                || first < 1 || last < first)
            {
                throw new ToolException(string.Format(Strings.Error_InvalidRange, part));
            }
            if (last > pageCount)
                throw new ToolException(string.Format(Strings.Error_RangeBeyondEnd, part, pageCount));
            ranges.Add((first, last));
        }
        if (ranges.Count == 0)
            throw new ToolException(string.Format(Strings.Error_InvalidRange, text));
        return ranges;
    }
}

public sealed class ImagesToPdfTool : PdfToolBase
{
    private const double PointsPerPixel = 72.0 / 96.0;
    private static readonly IReadOnlySet<FileKind> ImagesOnly = new HashSet<FileKind> { FileKind.Image };

    public override string Id => "pdf.fromImages";
    public override string Name => Strings.Tool_ImagesToPdf_Name;
    public override string Description => Strings.Tool_ImagesToPdf_Description;
    public override IReadOnlySet<FileKind> AcceptedKinds => ImagesOnly;
    public override bool CombinesInputs => true;
    public override int MinimumInputs => 1;
    public override bool ShowsSizeChange => false;

    public override IReadOnlyList<ToolOption> Options { get; } =
    [
        new ChoiceOption("pageSize", Strings.Option_PageSize,
        [
            new Choice("a4", "A4"),
            new Choice("image", Strings.Choice_PageSizeImage),
        ], "a4"),
        new BoolOption("margins", Strings.Option_Margins, true),
    ];

    public override long? EstimateOutputSize(IReadOnlyList<string> inputs, ToolSettings settings) => (long)(TotalSize(inputs) * 0.8);

    public override async Task<IReadOnlyList<string>> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var a4 = context.Settings.GetString("pageSize", "a4") == "a4";
        var margin = context.Settings.GetBool("margins", true) ? 36.0 : 0.0;
        var inputs = context.Inputs;

        var output = await WriteOutputAsync(context.Output, OutputName(inputs[0], ".pdf"), path => Task.Run(() =>
        {
            using var document = new PdfDocument();
            // Image data may be read when the document is saved: keep it alive until then.
            var images = new List<(MemoryStream Stream, XImage Image)>();
            try
            {
                for (var i = 0; i < inputs.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var stream = EncodeForPdf(inputs[i], out var width, out var height);
                    var image = XImage.FromStream(stream);
                    images.Add((stream, image));

                    var page = document.AddPage();
                    double imageWidth = width * PointsPerPixel, imageHeight = height * PointsPerPixel;
                    if (a4)
                    {
                        // Turn the page to match the picture.
                        page.Width = XUnit.FromMillimeter(width > height ? 297 : 210);
                        page.Height = XUnit.FromMillimeter(width > height ? 210 : 297);
                    }
                    else
                    {
                        page.Width = XUnit.FromPoint(imageWidth + 2 * margin);
                        page.Height = XUnit.FromPoint(imageHeight + 2 * margin);
                    }

                    var areaWidth = page.Width.Point - 2 * margin;
                    var areaHeight = page.Height.Point - 2 * margin;
                    var scale = Math.Min(areaWidth / imageWidth, areaHeight / imageHeight);
                    if (!a4)
                        scale = 1;
                    var drawWidth = imageWidth * scale;
                    var drawHeight = imageHeight * scale;

                    using var graphics = XGraphics.FromPdfPage(page);
                    graphics.DrawImage(image,
                        margin + (areaWidth - drawWidth) / 2,
                        margin + (areaHeight - drawHeight) / 2,
                        drawWidth, drawHeight);

                    context.Progress.Report((i + 1.0) / (inputs.Count + 1));
                }
                Save(document, path);
            }
            finally
            {
                foreach (var (stream, image) in images)
                {
                    image.Dispose();
                    stream.Dispose();
                }
            }
        }, cancellationToken));

        context.Progress.Report(1);
        return [output];
    }

    /// <summary>
    /// Re-encodes any image libvips can read (WebP, AVIF, HEIC, TIFF...) to JPEG, or PNG when it has
    /// transparency, with the EXIF rotation applied, since PDFsharp only embeds a few formats.
    /// </summary>
    private static MemoryStream EncodeForPdf(string path, out int width, out int height)
    {
        try
        {
            using var loaded = Image.NewFromFile(path);
            using var upright = loaded.Autorot();
            width = upright.Width;
            height = upright.Height;
            var buffer = upright.HasAlpha()
                ? upright.WriteToBuffer(".png")
                : upright.WriteToBuffer(".jpg", new VOption { { "Q", 90 } });
            return new MemoryStream(buffer);
        }
        catch (VipsException ex)
        {
            throw new ToolException(Strings.Error_ImageUnreadable, ex);
        }
    }
}

public sealed class PdfCompressTool : PdfToolBase
{
    public override string Id => "pdf.compress";
    public override string Name => Strings.Tool_PdfCompress_Name;
    public override string Description => Strings.Tool_PdfCompress_Description;

    public override IReadOnlyList<ToolOption> Options { get; } =
    [
        new ChoiceOption("level", Strings.Option_CompressionLevel,
        [
            new Choice("screen", Strings.Choice_PdfScreen),
            new Choice("ebook", Strings.Choice_PdfEbook),
            new Choice("printer", Strings.Choice_PdfPrinter),
        ], "ebook"),
    ];

    public override long? EstimateOutputSize(IReadOnlyList<string> inputs, ToolSettings settings)
    {
        var ratio = settings.GetString("level", "ebook") switch
        {
            "screen" => 0.3,
            "printer" => 0.8,
            _ => 0.5,
        };
        return (long)(TotalSize(inputs) * ratio);
    }

    public override async Task<IReadOnlyList<string>> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var ghostscript = ExternalTools.RequireGhostscript();
        var input = context.Inputs[0];
        var level = context.Settings.GetString("level", "ebook") switch
        {
            "screen" => "/screen",
            "printer" => "/printer",
            _ => "/ebook",
        };

        var output = await WriteOutputAsync(context.Output, OutputName(input, ".pdf", " - " + Strings.Suffix_Compressed), async path =>
        {
            var (exitCode, errors) = await ExternalTools.RunAsync(ghostscript,
            [
                "-sDEVICE=pdfwrite",
                "-dCompatibilityLevel=1.5",
                $"-dPDFSETTINGS={level}",
                "-dNOPAUSE",
                "-dBATCH",
                "-dQUIET",
                "-dSAFER",
                $"-sOutputFile={path}",
                input,
            ], cancellationToken);

            if (exitCode != 0)
                throw new ToolException(Strings.Error_PdfUnreadable, new Exception(errors));

            // Some PDFs are already optimal: never hand back a bigger file.
            if (new FileInfo(path).Length >= new FileInfo(input).Length)
                File.Copy(input, path, overwrite: true);
        });

        context.Progress.Report(1);
        return [output];
    }
}
