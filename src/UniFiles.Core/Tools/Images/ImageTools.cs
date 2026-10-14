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
        var decoded = HeifDecoder.IsHeif(input) ? await HeifDecoder.DecodeAsync(input, cancellationToken) : null;
        try
        {
            var output = await WriteOutputAsync(context.Output, OutputName(input, "." + plan.Format), path =>
                Task.Run(() => Process(decoded ?? input, path, plan, metadataFrom: decoded is null ? null : input), cancellationToken));
            context.Progress.Report(1);
            return [output];
        }
        finally
        {
            if (decoded is not null)
                OutputFolder.Release(decoded);
        }
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

    /// <param name="metadataFrom">Original file whose metadata goes with the pixels of <paramref name="input"/> (decoded HEIC).</param>
    internal static void Process(string input, string output, ImagePlan plan, string? metadataFrom = null)
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

        if (metadataFrom is not null)
        {
            using var pixels = image;
            image = HeifDecoder.WithMetadataOf(pixels, metadataFrom);
        }

        using (image)
        {
            try
            {
                if (plan.TargetBytes > 0)
                    File.WriteAllBytes(output, EncodeToFit(image, plan));
                else
                    Encode(image, plan, plan.Quality, (img, options) => { img.WriteToFile(output, options); return 0; });
            }
            catch (VipsException ex)
            {
                throw new ToolException(Strings.Error_ImageWrite, ex);
            }
        }
    }

    /// <summary>Encodes with the plan's format and metadata policy at the given quality.</summary>
    private static T Encode<T>(Image image, ImagePlan plan, int quality, Func<Image, VOption, T> write)
    {
        // Keep the color profile so colors stay right; drop EXIF, GPS, XMP and IPTC when asked.
        var keep = plan.StripMetadata ? Enums.ForeignKeep.Icc : Enums.ForeignKeep.All;
        var options = new VOption { { "keep", keep } };

        var encoded = image;
        switch (plan.Format)
        {
            case "jpg":
                options.Add("Q", quality);
                options.Add("optimize_coding", true);
                // JPEG has no transparency: flatten on white instead of black.
                if (image.HasAlpha())
                    encoded = image.Flatten(background: [255, 255, 255]);
                break;
            case "png":
                options.Add("compression", 9);
                if (quality < 100)
                {
                    // Palette quantization, as online "PNG compressors" do.
                    options.Add("palette", true);
                    options.Add("Q", quality);
                }
                break;
            case "webp":
            case "avif":
                options.Add("Q", quality);
                break;
        }

        try
        {
            return write(encoded, options);
        }
        finally
        {
            if (!ReferenceEquals(encoded, image))
                encoded.Dispose();
        }
    }

    /// <summary>
    /// Target size: the best quality that fits (binary search, at most the plan's quality), then
    /// smaller dimensions if even a low quality is too heavy. Returns the smallest result if nothing fits.
    /// </summary>
    internal static byte[] EncodeToFit(Image image, ImagePlan plan)
    {
        const int LowestQuality = 30;
        byte[]? smallest = null;
        var scale = 1.0;
        for (var attempt = 0; attempt < 8; attempt++, scale *= 0.8)
        {
            using var scaled = scale < 1 ? image.Resize(scale, kernel: Enums.Kernel.Lanczos3) : image.Copy();
            byte[] EncodeAt(int quality) => Encode(scaled, plan, quality, (img, options) => img.WriteToBuffer("." + plan.Format, options));

            var low = LowestQuality;
            var high = Math.Max(plan.Quality, LowestQuality);
            byte[]? best = null;
            while (low <= high)
            {
                var quality = (low + high) / 2;
                var bytes = EncodeAt(quality);
                if (smallest is null || bytes.Length < smallest.Length)
                    smallest = bytes;
                if (bytes.Length <= plan.TargetBytes)
                {
                    best = bytes;
                    low = quality + 1;
                }
                else
                {
                    high = quality - 1;
                }
            }
            if (best is not null)
                return best;
        }
        return smallest!;
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

/// <param name="TargetBytes">Maximum output size, or 0 to encode once at <paramref name="Quality"/>.</param>
public sealed record ImagePlan(string Format, int Quality, int MaxWidth, int MaxHeight, bool StripMetadata, long TargetBytes = 0);

public sealed class ImageCompressTool : ImageToolBase
{
    public override string Id => "image.compress";
    public override string Name => Strings.Tool_ImageCompress_Name;
    public override string Description => Strings.Tool_ImageCompress_Description;

    public override IReadOnlyList<ToolOption> Options { get; } =
    [
        new ChoiceOption("mode", Strings.Option_CompressMode,
        [
            new Choice("quality", Strings.Choice_ByQuality),
            new Choice("size", Strings.Choice_BySize),
        ], "quality"),
        QualityOption(75) with { VisibleWhenKey = "mode", VisibleWhenValue = "quality" },
        new NumberOption("targetKb", Strings.Option_TargetSize, 20, 50_000, 500, Strings.Unit_KB)
        {
            Hint = Strings.Hint_TargetSizeImage,
            VisibleWhenKey = "mode",
            VisibleWhenValue = "size",
        },
        StripOption(true),
    ];

    private static bool BySize(ToolSettings settings) => settings.GetString("mode", "quality") == "size";

    protected override ImagePlan Plan(string input, ToolSettings settings) => BySize(settings)
        ? new(SourceFormat(input), 90, 0, 0, settings.GetBool("strip", true), settings.GetInt("targetKb", 500) * 1024L)
        : new(SourceFormat(input), settings.GetInt("quality", 75), 0, 0, settings.GetBool("strip", true));

    public override long? EstimateOutputSize(IReadOnlyList<string> inputs, ToolSettings settings) => BySize(settings)
        ? inputs.Where(File.Exists).Sum(i => Math.Min(new FileInfo(i).Length, settings.GetInt("targetKb", 500) * 1024L))
        : base.EstimateOutputSize(inputs, settings);
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

/// <summary>iPhone photos (HEIC/HEIF) to JPG or PNG, which every app and website opens.</summary>
public sealed class ImageFromHeicTool : ImageToolBase
{
    public override string Id => "image.heic";
    public override string Name => Strings.Tool_ImageHeic_Name;
    public override string Description => Strings.Tool_ImageHeic_Description;

    public override bool Accepts(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".heic" or ".heif";

    public override IReadOnlyList<ToolOption> Options { get; } =
    [
        new ChoiceOption("format", Strings.Option_Format,
        [
            new Choice("jpg", "JPG"),
            new Choice("png", "PNG"),
        ], "jpg"),
        QualityOption(90),
        StripOption(false),
    ];

    protected override ImagePlan Plan(string input, ToolSettings settings) => new(
        settings.GetString("format", "jpg"),
        settings.GetInt("quality", 90),
        0,
        0,
        settings.GetBool("strip"));

    // HEIC is about twice as efficient as JPG at the same quality.
    public override long? EstimateOutputSize(IReadOnlyList<string> inputs, ToolSettings settings) =>
        (long)(TotalSize(inputs) * (settings.GetString("format", "jpg") == "png" ? 6 : 1.8));
}
