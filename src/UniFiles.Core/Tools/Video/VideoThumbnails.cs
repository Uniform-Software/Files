using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FFMpegCore;

namespace UniFiles.Core.Tools.Video;

public sealed record VideoInfo(TimeSpan Duration, int Width, int Height, bool HasAudio);

/// <summary>Reads what the video editor needs: duration, size, and a strip of thumbnails.</summary>
public static class VideoThumbnails
{
    public static async Task<VideoInfo> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var ffmpeg = ExternalTools.RequireFfmpeg();
        var analysis = await FFProbe.AnalyseAsync(path, new FFOptions { BinaryFolder = Path.GetDirectoryName(ffmpeg)! }, cancellationToken);
        var video = analysis.PrimaryVideoStream;
        return new VideoInfo(analysis.Duration, video?.Width ?? 0, video?.Height ?? 0, analysis.AudioStreams.Count > 0);
    }

    /// <summary>
    /// Extracts <paramref name="count"/> evenly spaced frames as small JPEGs in a cache folder and
    /// reports each one as soon as it is ready (index, file), so the strip fills in progressively.
    /// </summary>
    public static async Task ExtractAsync(string path, TimeSpan duration, int count, int height,
        Action<int, string> onThumbnail, CancellationToken cancellationToken)
    {
        var ffmpeg = ExternalTools.RequireFfmpeg();
        var folder = CacheFolder(path);
        Directory.CreateDirectory(folder);

        var options = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(Enumerable.Range(0, count), options, async (i, ct) =>
        {
            var file = Path.Combine(folder, $"{height}_{count}_{i:00}.jpg");
            if (!File.Exists(file))
            {
                // Middle of each slot; -ss before -i seeks to the nearest keyframe, which is fast.
                var time = duration.TotalSeconds * (i + 0.5) / count;
                var temp = file + ".tmp.jpg";
                var (exitCode, _) = await ExternalTools.RunAsync(ffmpeg,
                [
                    "-hide_banner", "-loglevel", "error",
                    "-ss", time.ToString("0.###", CultureInfo.InvariantCulture),
                    "-i", path,
                    "-frames:v", "1",
                    "-vf", $"scale=-2:{height}",
                    "-q:v", "4",
                    "-y", temp,
                ], ct);
                if (exitCode != 0 || !File.Exists(temp))
                    return;
                File.Move(temp, file, overwrite: true);
            }
            onThumbnail(i, file);
        });
    }

    /// <summary>
    /// Extracts the exact frame at <paramref name="seconds"/> (for scrubbing while paused) and returns
    /// the JPEG file, or null if it failed. Results are cached per tenth of a second.
    /// </summary>
    public static async Task<string?> ExtractFrameAsync(string path, double seconds, int height, CancellationToken cancellationToken)
    {
        var ffmpeg = ExternalTools.RequireFfmpeg();
        var folder = Path.Combine(CacheFolder(path), "frames");
        Directory.CreateDirectory(folder);
        var tenths = (long)Math.Round(seconds * 10);
        var file = Path.Combine(folder, $"{height}_{tenths}.jpg");
        if (File.Exists(file))
            return file;

        var temp = file + $".{Guid.NewGuid():N}.jpg";
        var (exitCode, _) = await ExternalTools.RunAsync(ffmpeg,
        [
            "-hide_banner", "-loglevel", "error",
            // Input seeking: jumps to the previous keyframe then decodes up to the exact time.
            "-ss", (tenths / 10.0).ToString("0.0", CultureInfo.InvariantCulture),
            "-i", path,
            "-frames:v", "1",
            "-vf", $"scale=-2:{height}",
            "-q:v", "3",
            "-y", temp,
        ], cancellationToken);
        if (exitCode != 0 || !File.Exists(temp))
            return null;
        File.Move(temp, file, overwrite: true);
        return file;
    }

    /// <summary>One folder per video version (path, size, date), in the system temp folder.</summary>
    private static string CacheFolder(string path)
    {
        var info = new FileInfo(path);
        var key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(Path.GetTempPath(), "UniFiles", "thumbnails", hash);
    }
}
