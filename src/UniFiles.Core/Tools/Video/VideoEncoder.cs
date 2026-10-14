using System.Runtime.InteropServices;

namespace UniFiles.Core.Tools.Video;

/// <summary>An H.264 encoder: ffmpeg arguments for a given quality.</summary>
/// <param name="Crf">x264 constant rate factor (lower is better: 18 visually lossless, 32 small).</param>
public sealed record H264Encoder(string Id, string DisplayName, bool IsHardware, Func<int, string> Arguments);

/// <summary>
/// Picks the H.264 encoder: the computer's video chip when one works (several times faster), x264
/// otherwise. The chip is tested once with a tiny real encode, since ffmpeg lists encoders whose
/// driver or hardware is missing. Hardware encoders make slightly bigger files at the same quality.
/// </summary>
public static class VideoEncoder
{
    private static readonly object Sync = new();
    private static Task<H264Encoder?>? _detection;

    public static readonly H264Encoder Software = new("libx264", "x264", false,
        crf => $"-c:v libx264 -crf {crf} -preset medium -pix_fmt yuv420p");

    /// <summary>Set from the preferences; off means x264 everywhere.</summary>
    public static bool HardwareEnabled { get; set; } = true;

    /// <summary>Hardware encoders worth trying on this system, best first.</summary>
    private static IEnumerable<H264Encoder> Candidates()
    {
        // VideoToolbox's constant-quality mode only exists on Apple Silicon.
        if (OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64)
        {
            yield return new("h264_videotoolbox", "Apple VideoToolbox", true,
                crf => $"-c:v h264_videotoolbox -q:v {Math.Clamp(100 - (int)(crf * 1.6), 30, 90)} -pix_fmt yuv420p");
        }
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            yield return new("h264_nvenc", "NVIDIA NVENC", true,
                crf => $"-c:v h264_nvenc -preset p5 -rc vbr -cq {crf} -b:v 0 -pix_fmt yuv420p");
        }
        if (OperatingSystem.IsWindows())
        {
            yield return new("h264_qsv", "Intel Quick Sync", true,
                crf => $"-c:v h264_qsv -preset medium -global_quality {crf} -pix_fmt nv12");
            yield return new("h264_amf", "AMD AMF", true,
                crf => $"-c:v h264_amf -quality balanced -rc cqp -qp_i {crf} -qp_p {crf} -pix_fmt nv12");
        }
    }

    /// <summary>The first hardware encoder that really works here, or null. Tested once per launch.</summary>
    public static Task<H264Encoder?> DetectHardwareAsync()
    {
        lock (Sync)
            return _detection ??= Task.Run(DetectAsync);
    }

    private static async Task<H264Encoder?> DetectAsync()
    {
        if (ExternalTools.FindFfmpeg() is not { } ffmpeg)
            return null;
        foreach (var candidate in Candidates())
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var arguments = new List<string> { "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=640x360:duration=0.2" };
                arguments.AddRange(candidate.Arguments(28).Split(' '));
                arguments.AddRange(["-f", "null", "-"]);
                var (exitCode, _) = await ExternalTools.RunAsync(ffmpeg, arguments, timeout.Token);
                if (exitCode == 0)
                    return candidate;
            }
            catch (OperationCanceledException)
            {
                // A driver that hangs counts as missing.
            }
        }
        return null;
    }

    /// <summary>The encoder to use now: hardware if enabled and available, x264 otherwise.</summary>
    public static async Task<H264Encoder> ChooseAsync() =>
        HardwareEnabled && await DetectHardwareAsync() is { } hardware ? hardware : Software;

    /// <summary>After a hardware encode failed mid-way (driver issue), use x264 until the next launch.</summary>
    public static void HardwareFailed()
    {
        lock (Sync)
            _detection = Task.FromResult<H264Encoder?>(null);
    }
}
