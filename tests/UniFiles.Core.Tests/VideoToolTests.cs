using UniFiles.Core.Tools;
using UniFiles.Core.Tools.Video;

namespace UniFiles.Core.Tests;

/// <summary>Runs the real ffmpeg; each test passes trivially on machines without it.</summary>
public class VideoToolTests
{
    private static async Task<string?> MakeClip(TempFolder temp)
    {
        if (ExternalTools.FindFfmpeg() is not { } ffmpeg)
            return null;
        var clip = temp.File("clip.mp4");
        var (exitCode, errors) = await ExternalTools.RunAsync(ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=4",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=4",
            "-c:v", "libx264", "-crf", "10", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", clip,
        ], CancellationToken.None);
        Assert.True(exitCode == 0, errors);
        return clip;
    }

    private static async Task<string> Run(IFileTool tool, string input, string outDir, Action<ToolSettings>? configure = null)
    {
        var settings = ToolSettings.FromDefaults(tool);
        configure?.Invoke(settings);
        var outputs = await tool.ExecuteAsync(new ToolContext
        {
            Inputs = [input],
            Settings = settings,
            Output = new OutputFolder(outDir),
        }, CancellationToken.None);
        return outputs.Single();
    }

    [Fact]
    public async Task Compress_makes_a_smaller_720p_mp4()
    {
        using var temp = new TempFolder();
        if (await MakeClip(temp) is not { } clip)
            return;

        var output = await Run(new VideoCompressTool(), clip, temp.Sub("out"), s =>
        {
            s["quality"] = "small";
            s["maxHeight"] = "240";
        });

        Assert.Equal(".mp4", Path.GetExtension(output));
        Assert.True(new FileInfo(output).Length < new FileInfo(clip).Length);
        var info = await VideoThumbnails.ProbeAsync(output, CancellationToken.None);
        Assert.Equal(240, info.Height);
    }

    [Fact]
    public async Task Hardware_detection_finds_a_working_encoder_or_none()
    {
        if (ExternalTools.FindFfmpeg() is null)
            return;

        var hardware = await VideoEncoder.DetectHardwareAsync();

        // Depends on the machine (virtual machines such as CI runners have no video chip): only check
        // that what was found is a hardware encoder meant for this system.
        if (hardware is not null)
        {
            Assert.True(hardware.IsHardware);
            Assert.Equal(OperatingSystem.IsMacOS(), hardware.Id == "h264_videotoolbox");
        }
        Assert.Same(hardware, await VideoEncoder.DetectHardwareAsync());
    }

    [Fact]
    public async Task Compress_to_a_target_size_stays_under_it()
    {
        using var temp = new TempFolder();
        if (ExternalTools.FindFfmpeg() is not { } ffmpeg)
            return;
        // Noise at crf 10: a few MB for 6 seconds, well above the 1 MB target.
        var clip = temp.File("noisy.mp4");
        var (exitCode, errors) = await ExternalTools.RunAsync(ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=6,noise=alls=30:allf=t",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=6",
            "-c:v", "libx264", "-crf", "10", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", clip,
        ], CancellationToken.None);
        Assert.True(exitCode == 0, errors);
        const long target = 1L << 20;
        Assert.True(new FileInfo(clip).Length > 2 * target);

        var output = await Run(new VideoCompressTool(), clip, temp.Sub("out"), s =>
        {
            s["mode"] = "size";
            s["targetMb"] = "1";
        });

        Assert.InRange(new FileInfo(output).Length, target / 2, target);
    }

    [Theory]
    [InlineData(25L << 20, 600, 1080, 0, 360)]   // 10 min in 25 MB: low bitrate, 360p
    [InlineData(25L << 20, 60, 1080, 0, 0)]      // 1 min in 25 MB: plenty, keep 1080p
    [InlineData(25L << 20, 60, 1080, 480, 480)]  // the user's cap wins
    [InlineData(10L << 20, 120, 360, 0, 0)]      // never upscale a 360p source
    public void Size_plan_picks_a_resolution_for_the_bitrate(long target, double seconds, int sourceHeight, int maxHeight, int expectedHeight)
    {
        var plan = SizePlan.For(target, seconds, sourceHeight, maxHeight, hasAudio: true);

        Assert.Equal(expectedHeight, plan.Height);
        Assert.True((plan.VideoKbps + plan.AudioKbps) * 1000.0 / 8 * seconds <= target);
    }

    [Fact]
    public void Size_plan_refuses_an_impossible_target() =>
        Assert.Throws<ToolException>(() => SizePlan.For(1L << 20, 3600, 1080, 0, hasAudio: true));

    [Theory]
    [InlineData("webm")]
    [InlineData("mkv")]
    [InlineData("mov")]
    public async Task Convert_writes_the_chosen_container(string format)
    {
        using var temp = new TempFolder();
        if (await MakeClip(temp) is not { } clip)
            return;

        var output = await Run(new VideoConvertTool(), clip, temp.Sub("out"), s => s["format"] = format);

        Assert.Equal("." + format, Path.GetExtension(output));
        var info = await VideoThumbnails.ProbeAsync(output, CancellationToken.None);
        Assert.InRange(info.Duration.TotalSeconds, 3.8, 4.2);
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("flac")]
    public async Task Audio_is_extracted(string format)
    {
        using var temp = new TempFolder();
        if (await MakeClip(temp) is not { } clip)
            return;

        var output = await Run(new AudioExtractTool(), clip, temp.Sub("out"), s => s["format"] = format);

        Assert.Equal("." + format, Path.GetExtension(output));
        Assert.True(new FileInfo(output).Length > 1000);
    }

    [Fact]
    public async Task Fast_single_cut_keeps_the_container()
    {
        using var temp = new TempFolder();
        if (await MakeClip(temp) is not { } clip)
            return;

        var output = await Run(new VideoTrimTool(), clip, temp.Sub("out"), s =>
        {
            s[VideoTrimTool.SegmentsKey] = "1-3";
            s["precise"] = false;
        });

        // Without re-encoding the start snaps back to the previous keyframe (here 0 s), the end stays.
        var info = await VideoThumbnails.ProbeAsync(output, CancellationToken.None);
        Assert.Equal(".mp4", Path.GetExtension(output));
        Assert.InRange(info.Duration.TotalSeconds, 1.9, 3.2);
    }
}
