using Avalonia.Media.Imaging;
using NetVips;
using UniFiles.Core.Tools;
using UniFiles.Core.Tools.Images;

namespace UniFiles.App.Services;

/// <summary>
/// Before/after pictures of an image tool on one file. The tool really runs (into a temporary
/// folder), so the "after" picture and its size are exactly what the job will produce.
/// </summary>
public sealed class ImagePreview : IDisposable
{
    private const int FitSize = 1400;
    private const int CropWidth = 900;
    private const int CropHeight = 560;

    private ImagePreview(Bitmap before, Bitmap after, long beforeBytes, long afterBytes)
    {
        Before = before;
        After = after;
        BeforeBytes = beforeBytes;
        AfterBytes = afterBytes;
    }

    public Bitmap Before { get; }

    public Bitmap After { get; }

    public long BeforeBytes { get; }

    public long AfterBytes { get; }

    /// <param name="actualPixels">A 1:1 crop of the center instead of the whole picture, to judge compression artifacts.</param>
    public static async Task<ImagePreview> CreateAsync(IFileTool tool, string input, ToolSettings settings, string tempFolder,
        bool actualPixels, CancellationToken cancellationToken)
    {
        var outputs = await tool.ExecuteAsync(new ToolContext
        {
            Inputs = [input],
            Settings = settings,
            Output = new OutputFolder(tempFolder),
        }, cancellationToken);
        var output = outputs[0];
        string? decoded = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // libvips can't decode HEIC pixels: show the ffmpeg-decoded original instead.
            if (HeifDecoder.IsHeif(input))
                decoded = await HeifDecoder.DecodeAsync(input, cancellationToken);
            var source = decoded ?? input;
            return await Task.Run(() => new ImagePreview(
                Render(source, actualPixels), Render(output, actualPixels),
                new FileInfo(input).Length, new FileInfo(output).Length), cancellationToken);
        }
        finally
        {
            OutputFolder.Release(output);
            if (decoded is not null)
                OutputFolder.Release(decoded);
        }
    }

    private static Bitmap Render(string path, bool actualPixels)
    {
        VipsSetup.Ensure();
        Image picture;
        if (actualPixels)
        {
            using var loaded = Image.NewFromFile(path);
            using var upright = loaded.Autorot();
            var width = Math.Min(CropWidth, upright.Width);
            var height = Math.Min(CropHeight, upright.Height);
            picture = upright.Crop((upright.Width - width) / 2, (upright.Height - height) / 2, width, height);
        }
        else
        {
            picture = Image.Thumbnail(path, FitSize, height: FitSize, size: Enums.Size.Down);
        }

        using (picture)
        {
            // Screens show 8-bit sRGB: convert anything else (16-bit PNG, CMYK JPEG, gray).
            using var display = picture.Colourspace(Enums.Interpretation.Srgb).Cast(Enums.BandFormat.Uchar);
            var png = display.PngsaveBuffer(compression: 1);
            return new Bitmap(new MemoryStream(png));
        }
    }

    public void Dispose()
    {
        Before.Dispose();
        After.Dispose();
    }
}
