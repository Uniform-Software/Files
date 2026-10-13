using NetVips;
using UniFiles.Core.Resources;

namespace UniFiles.Core.Tools.Images;

/// <summary>Shared pipeline of the image tools: load, auto-rotate, resize, encode.</summary>
public abstract class ImageToolBase : FileToolBase
{
    private static readonly IReadOnlySet<FileKind> Kinds = new HashSet<FileKind> { FileKind.Image };

    public override ToolModule Module => ToolModule.Images;

    public override IReadOnlySet<FileKind> AcceptedKinds => Kinds;

    // libvips already uses several threads per image; running a few images at once keeps every core busy.
    public override int MaxParallelism => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    protected static ChoiceOption FormatOption(bool includeKeep, string defaultValue) => new(
        "format",
        Strings.Option_Format,
        [
            .. includeKeep ? new[] { new Choice("keep", Strings.Choice_KeepFormat) } : [],
            new Choice("jpg", "JPG"),
            new Choice("png", "PNG"),
            new Choice("webp", "WebP"),
            new Choice("avif", "AVIF"),
        ],
        defaultValue);

    protected static NumberOption QualityOption(int defaultValue) =>
        new("quality", Strings.Option_Quality, 1, 100, defaultValue, "%") { Hint = Strings.Hint_Quality };

    protected static BoolOption StripOption(bool defaultValue) =>
        new("strip", Strings.Option_StripMetadata, defaultValue) { Hint = Strings.Hint_StripMetadata };

    public override async Task<IReadOnlyList<string>> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var input = context.Inputs[0];
        var plan = Plan(input, context.Settings);
        var output = await WriteOutputAsync(context.Output, OutputName(input, "." + plan.Format), path =>
            Task.Run(() => Process(input, path, plan), cancellationToken));
        context.Progress.Report(1);
        return [output];
    }

    /// <summary>Turns the user settings into concrete encoding choices for one file.</summary>
    protected abstract ImagePlan Plan(string input, ToolSettings settings);

    protected static string SourceFormat(string input)
    {
        var extension = Path.GetExtension(input).TrimStart('.').ToLowerInvariant();
        return extension switch
        {
            "jpeg" => "jpg",
            "jpg" or "png" or "webp" or "avif" => extension,
            // Formats libvips reads but that are poor targets fall back to the closest common one.
            "gif" or "bmp" or "tif" or "tiff" => "png",
            _ => "jpg",
        };
    }

    internal static void Process(string input, string output, ImagePlan plan)
    {
        Image image;
        try
        {
            image = plan.MaxWidth > 0 || plan.MaxHeight > 0
                // Thumbnail shrinks while decoding (fast, low memory) and applies the EXIF rotation.
                ? Image.Thumbnail(input, plan.MaxWidth > 0 ? plan.MaxWidth : 100_000,
                    height: plan.MaxHeight > 0 ? plan.MaxHeight : 100_000, size: Enums.Size.Down)
                : LoadUpright(input);
        }
        catch (VipsException ex)
        {
            throw new ToolException(Strings.Error_ImageUnreadable, ex);
        }

        using (image)
        {
            // Keep the color profile so colors stay right; drop EXIF, GPS, XMP and IPTC when asked.
            var keep = plan.StripMetadata ? Enums.ForeignKeep.Icc : Enums.ForeignKeep.All;
            var options = new VOption { { "keep", keep } };

            var encoded = image;
            switch (plan.Format)
            {
                case "jpg":
                    options.Add("Q", plan.Quality);
                    options.Add("optimize_coding", true);
                    // JPEG has no transparency: flatten on white instead of black.
                    if (image.HasAlpha())
                        encoded = image.Flatten(background: [255, 255, 255]);
                    break;
                case "png":
                    options.Add("compression", 9);
                    if (plan.Quality < 100)
                    {
                        // Palette quantization, as online "PNG compressors" do.
                        options.Add("palette", true);
                        options.Add("Q", plan.Quality);
                    }
                    break;
                case "webp":
                case "avif":
                    options.Add("Q", plan.Quality);
                    break;
            }

            try
            {
                encoded.WriteToFile(output, options);
            }
            catch (VipsException ex)
            {
                throw new ToolException(Strings.Error_ImageWrite, ex);
            }
            finally
            {
                if (!ReferenceEquals(encoded, image))
                    encoded.Dispose();
            }
        }
    }

    private static Image LoadUpright(string input)
    {
        using var loaded = Image.NewFromFile(input);
        return loaded.Autorot();
    }

    /// <summary>Very rough ratio of output size to input size, enough for a "≈ −60 %" hint.</summary>
    protected static double CompressionRatio(string sourceFormat, string targetFormat, int quality)
    {
        var q = quality / 100.0;
        var ratio = targetFormat switch
        {
            "jpg" => 0.15 + 0.55 * q * q,
            "webp" => 0.10 + 0.45 * q * q,
            "avif" => 0.07 + 0.35 * q * q,
            "png" => quality < 100 ? 0.35 : 0.9,
            _ => 1.0,
        };
        // Already compressed sources shrink much less than PNG or TIFF ones.
        if (sourceFormat is "jpg" or "webp" or "avif")
            ratio = Math.Min(1.0, ratio * 2.2);
        return ratio;
    }

    public override long? EstimateOutputSize(IReadOnlyList<string> inputs, ToolSettings settings)
    {
        double total = 0;
        foreach (var input in inputs)
        {
            if (!File.Exists(input))
                continue;
            var plan = Plan(input, settings);
            var resizeFactor = plan.MaxWidth > 0 || plan.MaxHeight > 0 ? 0.5 : 1.0;
            total += new FileInfo(input).Length * CompressionRatio(SourceFormat(input), plan.Format, plan.Quality) * resizeFactor;
        }
        return (long)total;
    }
}

public sealed record ImagePlan(string Format, int Quality, int MaxWidth, int MaxHeight, bool StripMetadata);

public sealed class ImageCompressTool : ImageToolBase
{
    public override string Id => "image.compress";
    public override string Name => Strings.Tool_ImageCompress_Name;
    public override string Description => Strings.Tool_ImageCompress_Description;

    public override IReadOnlyList<ToolOption> Options { get; } =
    [
        QualityOption(75),
        StripOption(true),
    ];

    protected override ImagePlan Plan(string input, ToolSettings settings) =>
        new(SourceFormat(input), settings.GetInt("quality", 75), 0, 0, settings.GetBool("strip", true));
}

public sealed class ImageResizeTool : ImageToolBase
{
    public override string Id => "image.resize";
    public override string Name => Strings.Tool_ImageResize_Name;
    public override string Description => Strings.Tool_ImageResize_Description;

    public override IReadOnlyList<ToolOption> Options { get; } =
    [
        new NumberOption("maxWidth", Strings.Option_MaxWidth, 0, 10_000, 1920, "px") { Hint = Strings.Hint_ZeroNoLimit },
        new NumberOption("maxHeight", Strings.Option_MaxHeight, 0, 10_000, 1920, "px") { Hint = Strings.Hint_ZeroNoLimit },
        QualityOption(85),
        StripOption(false),
    ];

    protected override ImagePlan Plan(string input, ToolSettings settings) => new(
        SourceFormat(input),
        settings.GetInt("quality", 85),
        settings.GetInt("maxWidth", 1920),
        settings.GetInt("maxHeight", 1920),
        settings.GetBool("strip"));
}

public sealed class ImageConvertTool : ImageToolBase
{
    public override string Id => "image.convert";
    public override string Name => Strings.Tool_ImageConvert_Name;
    public override string Description => Strings.Tool_ImageConvert_Description;

    public override IReadOnlyList<ToolOption> Options { get; } =
    [
        FormatOption(includeKeep: false, "webp"),
        QualityOption(85),
        StripOption(false),
    ];

    protected override ImagePlan Plan(string input, ToolSettings settings) => new(
        settings.GetString("format", "webp"),
        settings.GetInt("quality", 85),
        0,
        0,
        settings.GetBool("strip"));
}

public sealed class ImageStripMetadataTool : ImageToolBase
{
    public override string Id => "image.strip";
    public override string Name => Strings.Tool_ImageStrip_Name;
    public override string Description => Strings.Tool_ImageStrip_Description;

    public override IReadOnlyList<ToolOption> Options { get; } = [];

    // High quality: the goal is to remove data, not to recompress visibly.
    protected override ImagePlan Plan(string input, ToolSettings settings) =>
        new(SourceFormat(input), SourceFormat(input) == "png" ? 100 : 95, 0, 0, StripMetadata: true);

    public override long? EstimateOutputSize(IReadOnlyList<string> inputs, ToolSettings settings) => TotalSize(inputs);
}
