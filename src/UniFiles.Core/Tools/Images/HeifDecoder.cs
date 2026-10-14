using NetVips;
using UniFiles.Core.Resources;

namespace UniFiles.Core.Tools.Images;

/// <summary>
/// HEIC/HEIF photos (iPhone): the bundled libvips reads their metadata but has no HEVC decoder,
/// so ffmpeg decodes the pixels (tiled grids and rotation included) into a temporary PNG.
/// </summary>
public static class HeifDecoder
{
    private static readonly string[] CopiedFields = ["exif-data", "xmp-data", "icc-profile-data"];

    public static bool IsHeif(string path) => Path.GetExtension(path).ToLowerInvariant() is ".heic" or ".heif";

    /// <summary>Decodes the photo to a temporary PNG; the caller deletes it.</summary>
    public static async Task<string> DecodeAsync(string input, CancellationToken cancellationToken)
    {
        var ffmpeg = ExternalTools.RequireFfmpeg();
        var temp = Path.Combine(Path.GetTempPath(), $"unifiles-{Guid.NewGuid():N}.png");
        var (exitCode, _) = await ExternalTools.RunAsync(ffmpeg,
            ["-v", "error", "-y", "-i", input, "-frames:v", "1", "-pix_fmt", "rgb24", temp], cancellationToken);
        if (exitCode != 0 || !File.Exists(temp))
        {
            OutputFolder.Release(temp);
            throw new ToolException(Strings.Error_ImageUnreadable);
        }
        return temp;
    }

    /// <summary>
    /// Copies EXIF, XMP and the color profile of the original photo onto decoded pixels. The pixels
    /// are already upright, so the orientation is reset.
    /// </summary>
    public static Image WithMetadataOf(Image pixels, string original)
    {
        using var header = Image.NewFromFile(original); // header only: the pixels are never decoded
        return pixels.Mutate(mutable =>
        {
            foreach (var field in CopiedFields)
            {
                if (header.GetTypeOf(field) is var type and not 0)
                    mutable.Set(type, field, header.Get(field));
            }
            mutable.Set(GValue.GIntType, "orientation", 1);
        });
    }
}
