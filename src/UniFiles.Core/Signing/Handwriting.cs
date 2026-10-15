using System.Text.Json;
using NetVips;
using UniFiles.Core.Resources;
using UniFiles.Core.Tools;
using UniFiles.Core.Tools.Images;

namespace UniFiles.Core.Signing;

/// <summary>
/// A handwritten signature drawn with the mouse, trackpad or pen: strokes of points, normalized to
/// a box (x from 0 to 1, y from 0 to 1 downwards) whose width / height is <see cref="AspectRatio"/>.
/// </summary>
public sealed class Handwriting
{
    public double AspectRatio { get; set; } = 3;

    /// <summary>Each stroke: x0, y0, x1, y1… (normalized).</summary>
    public List<List<double>> Strokes { get; set; } = [];

    public bool IsEmpty => Strokes.All(s => s.Count < 4);
}

/// <summary>
/// The handwritten signature of each certificate, kept in the app's data folder to be reused:
/// drawn strokes (handwriting/&lt;id&gt;.json) or an image made transparent around the ink
/// (handwriting/&lt;id&gt;.png), where id is the certificate's <see cref="SigningIdentity.Id"/>.
/// A certificate has one or the other, or none.
/// </summary>
public sealed class HandwritingStore(string folder)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private string Folder => Path.Combine(folder, "handwriting");

    private string DrawingFile(string id) => Path.Combine(Folder, id + ".json");

    private string ImageFile(string id) => Path.Combine(Folder, id + ".png");

    public Handwriting? Drawing(string id)
    {
        try
        {
            return File.Exists(DrawingFile(id)) ? JsonSerializer.Deserialize<Handwriting>(File.ReadAllText(DrawingFile(id))) is { IsEmpty: false } drawing ? drawing : null : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Saves a drawing (replacing an image); an empty drawing removes the signature.</summary>
    public void SaveDrawing(string id, Handwriting drawing)
    {
        Clear(id);
        if (drawing.IsEmpty)
            return;
        Directory.CreateDirectory(Folder);
        File.WriteAllText(DrawingFile(id), JsonSerializer.Serialize(drawing, Json));
    }

    /// <summary>The prepared image (PNG with transparency), or null.</summary>
    public byte[]? Image(string id) => File.Exists(ImageFile(id)) ? File.ReadAllBytes(ImageFile(id)) : null;

    public string? ImagePath(string id) => File.Exists(ImageFile(id)) ? ImageFile(id) : null;

    /// <summary>"drawn", "image" or "none".</summary>
    public string KindOf(string id) => Drawing(id) is not null ? "drawn" : ImagePath(id) is not null ? "image" : "none";

    /// <summary>
    /// A photo or scan of a signature: light background made transparent (the ink keeps its color),
    /// cropped to the ink, at most 1 200 px wide.
    /// </summary>
    public void ImportImage(string id, string path)
    {
        VipsSetup.Ensure();
        byte[] png;
        try
        {
            using var loaded = NetVips.Image.NewFromFile(path);
            using var upright = loaded.Autorot();
            using var srgb = upright.Colourspace(Enums.Interpretation.Srgb);
            using var flat = srgb.HasAlpha() ? srgb.Flatten(background: [255, 255, 255]) : srgb.Copy();
            using var rgb = flat.ExtractBand(0, 3);
            using var gray = rgb.Colourspace(Enums.Interpretation.Bw);
            // Darker than the paper becomes opaque: 235+ fully transparent, 120- fully opaque.
            using var alpha = ((235 - gray) * (255.0 / 115)).Cast(Enums.BandFormat.Uchar);
            using var withAlpha = rgb.Bandjoin(alpha);
            var (left, top, width, height) = InkBox(alpha);
            if (width < 4 || height < 4)
                throw new ToolException(Strings.Sign_HandwritingEmpty);
            using var cropped = withAlpha.Crop(left, top, width, height);
            using var sized = cropped.Width > 1200 ? cropped.Resize(1200.0 / cropped.Width) : cropped.Copy();
            png = sized.PngsaveBuffer(compression: 9);
        }
        catch (VipsException ex)
        {
            throw new ToolException(Strings.Error_ImageUnreadable, ex);
        }
        Clear(id);
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(ImageFile(id), png);
    }

    public void Clear(string id)
    {
        foreach (var file in new[] { DrawingFile(id), ImageFile(id) })
            if (File.Exists(file))
                File.Delete(file);
    }

    /// <summary>The box of the ink: pixels more than slightly opaque.</summary>
    private static (int Left, int Top, int Width, int Height) InkBox(NetVips.Image alpha)
    {
        var pixels = alpha.WriteToMemory<byte>();
        int width = alpha.Width, height = alpha.Height;
        int left = width, top = height, right = -1, bottom = -1;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                if (pixels[y * width + x] > 30)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
        return right < 0 ? (0, 0, 0, 0) : (left, top, right - left + 1, bottom - top + 1);
    }
}
