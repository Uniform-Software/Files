using NetVips;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UniFiles.Core.Resources;

namespace UniFiles.Core.Tools.Pdf;

/// <summary>One image per page (PNG or JPG), drawn by PDFium at the chosen resolution.</summary>
public sealed class PdfToImagesTool : PdfToolBase
{
    public override string Id => "pdf.toImages";
    public override string Name => Strings.Tool_PdfToImages_Name;
    public override string Description => Strings.Tool_PdfToImages_Description;
    public override bool ShowsSizeChange => false;
    public override FileKind OutputKind(FileKind input) => FileKind.Image;

    public override IReadOnlyList<ToolOption> Options { get; } =
    [
        new ChoiceOption("format", Strings.Option_Format,
        [
            new Choice("png", "PNG"),
            new Choice("jpg", "JPG"),
        ], "png"),
        new ChoiceOption("dpi", Strings.Option_Resolution,
        [
            new Choice("72", Strings.Choice_DpiScreen),
            new Choice("150", Strings.Choice_DpiStandard),
            new Choice("300", Strings.Choice_DpiPrint),
        ], "150"),
        new ChoiceOption("pages", Strings.Option_Pages,
        [
            new Choice("all", Strings.Choice_AllPages),
            new Choice("ranges", Strings.Choice_SomePages),
        ], "all"),
        new TextOption("ranges", Strings.Option_Ranges, "1")
        {
            Hint = Strings.Hint_Ranges,
            VisibleWhenKey = "pages",
            VisibleWhenValue = "ranges",
        },
    ];

    public override async Task<IReadOnlyList<string>> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var input = context.Inputs[0];
        var format = context.Settings.GetString("format", "png");
        var dpi = Math.Clamp(context.Settings.GetInt("dpi", 150), 36, 600);

        using var renderer = await Task.Run(() => PdfRenderer.Open(input), cancellationToken);
        var pages = context.Settings.GetString("pages", "all") == "ranges"
            ? PdfSplitTool.ParseRanges(context.Settings.GetString("ranges"), renderer.PageCount)
                .SelectMany(r => Enumerable.Range(r.First, r.Last - r.First + 1)).Distinct().ToList()
            : Enumerable.Range(1, renderer.PageCount).ToList();

        // Several pages go in a folder named after the PDF.
        var stem = Path.GetFileNameWithoutExtension(input);
        var output = pages.Count > 1 ? new OutputFolder(Path.Combine(context.Output.Path, OutputFolder.Sanitize(stem))) : context.Output;
        var digits = Math.Max(2, renderer.PageCount.ToString().Length);

        var results = new List<string>();
        for (var i = 0; i < pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var number = pages[i];
            var name = $"{stem} - {Strings.Suffix_Page} {number.ToString().PadLeft(digits, '0')}.{format}";
            results.Add(await WriteOutputAsync(output, name, path => Task.Run(() =>
            {
                var pixels = renderer.RenderAtDpi(number - 1, dpi, out var width, out var height);
                using var bgra = Image.NewFromMemory(pixels, width, height, 4, Enums.BandFormat.Uchar);
                using var rgb = bgra[2].Bandjoin(bgra[1], bgra[0]).Copy(interpretation: Enums.Interpretation.Srgb, xres: dpi / 25.4, yres: dpi / 25.4);
                rgb.WriteToFile(path, format == "jpg" ? new VOption { { "Q", 90 } } : new VOption { { "compression", 6 } });
            }, cancellationToken)));
            context.Progress.Report((i + 1.0) / pages.Count);
        }
        return results;
    }
}

/// <summary>Removes the author, software, dates and XMP metadata of a PDF; the pages are untouched.</summary>
public sealed class PdfStripMetadataTool : PdfToolBase
{
    public override string Id => "pdf.strip";
    public override string Name => Strings.Tool_PdfStrip_Name;
    public override string Description => Strings.Tool_PdfStrip_Description;
    public override bool ShowsSizeChange => false;

    public override IReadOnlyList<ToolOption> Options { get; } =
    [
        new BoolOption("keepTitle", Strings.Option_KeepTitle, false),
    ];

    public override long? EstimateOutputSize(IReadOnlyList<string> inputs, ToolSettings settings) => TotalSize(inputs);

    public override async Task<IReadOnlyList<string>> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var input = context.Inputs[0];
        var keepTitle = context.Settings.GetBool("keepTitle");
        var output = await WriteOutputAsync(context.Output, Path.GetFileName(input), path => Task.Run(() =>
        {
            PdfDocument document;
            try
            {
                document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new ToolException(Strings.Error_PdfUnreadable, ex);
            }
            using (document)
            {
                var title = document.Info.Title;
                document.Info.Elements.Clear();
                if (keepTitle && !string.IsNullOrEmpty(title))
                    document.Info.Title = title;
                document.Internals.Catalog.Elements.Remove("/Metadata");
                foreach (var page in document.Pages)
                    page.Elements.Remove("/Metadata");
                document.Save(path);
            }
        }, cancellationToken));
        context.Progress.Report(1);
        return [output];
    }
}
