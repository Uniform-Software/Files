using System.Globalization;
using FFMpegCore;
using FFMpegCore.Exceptions;
using UniFiles.Core.Resources;

namespace UniFiles.Core.Tools.Video;

/// <summary>Shared ffmpeg plumbing. ffmpeg streams the files, so multi-gigabyte videos never sit in memory.</summary>
public abstract class VideoToolBase : FileToolBase
{
    protected static readonly IReadOnlySet<FileKind> VideoOnly = new HashSet<FileKind> { FileKind.Video };

    public override ToolModule Module => ToolModule.Video;

    public override IReadOnlySet<FileKind> AcceptedKinds => VideoOnly;

    protected static FFOptions FfOptions()
    {
        var ffmpeg = ExternalTools.RequireFfmpeg();
        return new FFOptions { BinaryFolder = Path.GetDirectoryName(ffmpeg)! };
    }

    /// <summary>Runs ffmpeg on one file and reports progress from its timestamps.</summary>
    protected static async Task<string> TranscodeAsync(
        ToolContext context,
        string outputName,
        Action<FFMpegArgumentOptions> outputOptions,
        CancellationToken cancellationToken,
        Action<FFMpegArgumentOptions>? inputOptions = null,
        TimeSpan? expectedDuration = null)
    {
        var input = context.Inputs[0];
        var duration = expectedDuration ?? (await AnalyseAsync(input, cancellationToken)).Duration;

        return await WriteOutputAsync(context.Output, outputName, path =>
            RunAsync(input, path, outputOptions, context.Progress.Report, duration, cancellationToken, inputOptions));
    }

    /// <summary>
    /// Like <see cref="TranscodeAsync"/> for an H.264 output: <paramref name="arguments"/> receives the
    /// encoder arguments for <paramref name="crf"/> (video chip when available, x264 otherwise). If the
    /// chip fails, the file is redone with x264.
    /// </summary>
    protected static async Task<string> TranscodeH264Async(
        ToolContext context,
        string outputName,
        int crf,
        Func<string, string> arguments,
        CancellationToken cancellationToken,
        Action<FFMpegArgumentOptions>? inputOptions = null,
        TimeSpan? expectedDuration = null)
    {
        var encoder = await VideoEncoder.ChooseAsync();
        if (encoder.IsHardware)
        {
            try
            {
                return await TranscodeAsync(context, outputName, o => o.WithCustomArgument(arguments(encoder.Arguments(crf))),
                    cancellationToken, inputOptions, expectedDuration);
            }
            catch (ToolException) when (!cancellationToken.IsCancellationRequested)
            {
                VideoEncoder.HardwareFailed();
            }
        }
        return await TranscodeAsync(context, outputName, o => o.WithCustomArgument(arguments(VideoEncoder.Software.Arguments(crf))),
            cancellationToken, inputOptions, expectedDuration);
    }

    /// <summary>Reads duration and streams; a file ffprobe can't read gives a plain-language error.</summary>
    protected static async Task<IMediaAnalysis> AnalyseAsync(string input, CancellationToken cancellationToken)
    {
        try
        {
            return await FFProbe.AnalyseAsync(input, FfOptions(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ToolException)
        {
            throw new ToolException(Strings.Error_MediaUnreadable, ex);
        }
    }

    /// <summary>One ffmpeg run, progress from 0 to 1 reported to <paramref name="progress"/>.</summary>
    protected static async Task RunAsync(
        string input,
        string output,
        Action<FFMpegArgumentOptions> outputOptions,
        Action<double> progress,
        TimeSpan duration,
        CancellationToken cancellationToken,
        Action<FFMpegArgumentOptions>? inputOptions = null)
    {
        try
        {
            await FFMpegArguments
                .FromFileInput(input, verifyExists: true, inputOptions)
                .OutputToFile(output, overwrite: true, outputOptions)
                .NotifyOnProgress(percent => progress(Math.Clamp(percent / 100.0, 0, 1)), duration)
                .CancellableThrough(cancellationToken)
                .ProcessAsynchronously(throwOnError: true, FfOptions());
        }
        catch (Exception ex) when (ex is FFMpegException or FFMpegArgumentException && !cancellationToken.IsCancellationRequested)
        {
            throw new ToolException(Strings.Error_MediaConversion, ex);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Accepts "90", "1:30", "01:02:03" or "1:30.5".</summary>
    public static TimeSpan ParseTime(string text)
    {
        text = text.Trim().Replace(',', '.');
        if (string.IsNullOrEmpty(text))
            return TimeSpan.Zero;

        var parts = text.Split(':');
        if (parts.Length > 3 || !parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            throw new ToolException(string.Format(Strings.Error_InvalidTime, text));

        var seconds = parts.Aggregate(0.0, (total, part) => total * 60 + double.Parse(part, CultureInfo.InvariantCulture));
        return TimeSpan.FromSeconds(seconds);
    }

    protected static string FormatTime(TimeSpan time) =>
        time.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
}

public sealed class VideoCompressTool : VideoToolBase
{
    public override string Id => "video.compress";
    public override bool KeepsOriginalIfLarger => true;
    public override string Name => Strings.Tool_VideoCompress_Name;
    public override string Description => Strings.Tool_VideoCompress_Description;

    public override IReadOnlyList<ToolOption> Options { get; } =
    [
        new ChoiceOption("mode", Strings.Option_CompressMode,
        [
            new Choice("quality", Strings.Choice_ByQuality),
            new Choice("size", Strings.Choice_BySize),
        ], "quality"),
        new ChoiceOption("quality", Strings.Option_Quality,
        [
            new Choice("high", Strings.Choice_QualityHigh),
            new Choice("balanced", Strings.Choice_QualityBalanced),
            new Choice("small", Strings.Choice_QualitySmall),
        ], "balanced") { VisibleWhenKey = "mode", VisibleWhenValue = "quality" },
        new ChoiceOption("targetMb", Strings.Option_TargetSize,
        [
            new Choice("10", Strings.Choice_Target10),
            new Choice("25", Strings.Choice_Target25),
            new Choice("50", "50 " + Strings.Unit_MB),
            new Choice("100", Strings.Choice_Target100),
            new Choice("500", "500 " + Strings.Unit_MB),
        ], "25") { Hint = Strings.Hint_TargetSizeVideo, VisibleWhenKey = "mode", VisibleWhenValue = "size" },
        new ChoiceOption("maxHeight", Strings.Option_Resolution,
        [
            new Choice("0", Strings.Choice_KeepResolution),
            new Choice("1080", "1080p"),
            new Choice("720", "720p"),
            new Choice("480", "480p"),
        ], "0"),
    ];

    private static bool BySize(ToolSettings settings) => settings.GetString("mode", "quality") == "size";

    private static long TargetBytes(ToolSettings settings) => settings.GetInt("targetMb", 25) * 1024L * 1024;

    public override long? EstimateOutputSize(IReadOnlyList<string> inputs, ToolSettings settings)
    {
        if (BySize(settings))
            return inputs.Where(File.Exists).Sum(i => Math.Min(new FileInfo(i).Length, TargetBytes(settings)));

        var ratio = settings.GetString("quality", "balanced") switch
        {
            "high" => 0.6,
            "small" => 0.2,
            _ => 0.35,
        };
        if (settings.GetInt("maxHeight") is > 0 and <= 720)
            ratio *= 0.6;
        return (long)(TotalSize(inputs) * ratio);
    }

    public override async Task<IReadOnlyList<string>> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (BySize(context.Settings))
            return [await CompressToSizeAsync(context, cancellationToken)];

        var input = context.Inputs[0];
        var crf = context.Settings.GetString("quality", "balanced") switch
        {
            "high" => 23,
            "small" => 32,
            _ => 28,
        };
        var maxHeight = context.Settings.GetInt("maxHeight");

        var scale = maxHeight > 0 ? $"-vf scale=-2:'trunc(min({maxHeight},ih)/2)*2' " : "";
        var output = await TranscodeH264Async(context, OutputName(input, ".mp4", " - " + Strings.Suffix_Compressed), crf,
            video => $"{scale}{video} -c:a aac -b:a 128k -movflags +faststart", cancellationToken);

        return [output];
    }

    /// <summary>
    /// Target size: two-pass H.264 at the bitrate that fills the target (with a margin for the
    /// container), lowering the resolution when that bitrate is too low for a sharp picture.
    /// </summary>
    private async Task<string> CompressToSizeAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var input = context.Inputs[0];
        var analysis = await AnalyseAsync(input, cancellationToken);
        var plan = SizePlan.For(TargetBytes(context.Settings), analysis.Duration.TotalSeconds, analysis.PrimaryVideoStream?.Height ?? 0,
            context.Settings.GetInt("maxHeight"), analysis.PrimaryAudioStream is not null);

        var target = TargetBytes(context.Settings);
        var scale = plan.Height > 0 ? $"-vf scale=-2:{plan.Height} " : "";
        var audio = plan.AudioKbps > 0 ? $"-c:a aac -b:a {plan.AudioKbps}k" : "-an";
        string Video(int kbps) => $"{scale}-c:v libx264 -b:v {kbps}k -preset medium -pix_fmt yuv420p";
        var passLog = Path.Combine(Path.GetTempPath(), $"unifiles-{Guid.NewGuid():N}");
        try
        {
            await RunAsync(input, OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
                o => o.WithCustomArgument($"{Video(plan.VideoKbps)} -pass 1 -passlogfile \"{passLog}\" -an -f mp4"),
                p => context.Progress.Report(p / 2), analysis.Duration, cancellationToken);

            return await WriteOutputAsync(context.Output, OutputName(input, ".mp4", " - " + Strings.Suffix_Compressed), async path =>
            {
                // x264 can overshoot a little (short or noisy videos): the first pass stays valid,
                // so the second one is redone at a corrected bitrate until the file fits.
                var kbps = plan.VideoKbps;
                for (var attempt = 0; ; attempt++)
                {
                    await RunAsync(input, path,
                        o => o.WithCustomArgument($"{Video(kbps)} -pass 2 -passlogfile \"{passLog}\" {audio} -movflags +faststart"),
                        p => context.Progress.Report(0.5 + p / 2), analysis.Duration, cancellationToken);
                    var size = new FileInfo(path).Length;
                    if (size <= target || attempt == 2)
                        break;
                    kbps = Math.Max(1, (int)(kbps * (double)target / size * 0.95));
                }
            });
        }
        finally
        {
            foreach (var log in Directory.EnumerateFiles(Path.GetTempPath(), Path.GetFileName(passLog) + "*"))
                OutputFolder.Release(log);
        }
    }
}

public sealed class VideoConvertTool : VideoToolBase
{
    public override string Id => "video.convert";
    public override string Name => Strings.Tool_VideoConvert_Name;
    public override string Description => Strings.Tool_VideoConvert_Description;

    public override IReadOnlyList<ToolOption> Options { get; } =
    [
        new ChoiceOption("format", Strings.Option_Format,
        [
            new Choice("mp4", "MP4 (H.264)"),
            new Choice("webm", "WebM (VP9)"),
            new Choice("mov", "MOV"),
            new Choice("mkv", "MKV"),
        ], "mp4"),
    ];

    public override async Task<IReadOnlyList<string>> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var input = context.Inputs[0];
        var format = context.Settings.GetString("format", "mp4");
        var name = OutputName(input, "." + format);
        var output = format switch
        {
            "webm" => await TranscodeAsync(context, name,
                o => o.WithCustomArgument("-c:v libvpx-vp9 -crf 32 -b:v 0 -row-mt 1 -c:a libopus -b:a 128k"), cancellationToken),
            // MKV accepts almost any codec: copy the streams, no quality loss.
            "mkv" => await TranscodeAsync(context, name, o => o.WithCustomArgument("-map 0 -c copy"), cancellationToken),
            _ => await TranscodeH264Async(context, name, 20,
                video => $"{video} -c:a aac -b:a 192k -movflags +faststart", cancellationToken),
        };
        return [output];
    }
}

public sealed class AudioExtractTool : VideoToolBase
{
    private static readonly IReadOnlySet<FileKind> VideoAndAudio = new HashSet<FileKind> { FileKind.Video, FileKind.Audio };

    public override string Id => "audio.extract";
    public override FileKind OutputKind(FileKind input) => FileKind.Audio;
    public override string Name => Strings.Tool_AudioExtract_Name;
    public override string Description => Strings.Tool_AudioExtract_Description;
    public override IReadOnlySet<FileKind> AcceptedKinds => VideoAndAudio;

    public override IReadOnlyList<ToolOption> Options { get; } =
    [
        new ChoiceOption("format", Strings.Option_Format,
        [
            new Choice("mp3", "MP3"),
            new Choice("m4a", "M4A (AAC)"),
            new Choice("ogg", "OGG (Opus)"),
            new Choice("flac", Strings.Choice_FlacLossless),
            new Choice("wav", "WAV"),
        ], "mp3"),
        new ChoiceOption("bitrate", Strings.Option_Bitrate,
        [
            new Choice("128", "128 kb/s"),
            new Choice("192", "192 kb/s"),
            new Choice("320", "320 kb/s"),
        ], "192") { Hint = Strings.Hint_Bitrate },
    ];

    public override async Task<IReadOnlyList<string>> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var input = context.Inputs[0];
        var format = context.Settings.GetString("format", "mp3");
        var bitrate = context.Settings.GetInt("bitrate", 192);
        var codec = format switch
        {
            "m4a" => $"-c:a aac -b:a {bitrate}k",
            "ogg" => $"-c:a libopus -b:a {Math.Min(bitrate, 256)}k",
            "flac" => "-c:a flac",
            "wav" => "-c:a pcm_s16le",
            _ => $"-c:a libmp3lame -b:a {bitrate}k",
        };

        var output = await TranscodeAsync(context, OutputName(input, "." + format), o => o.WithCustomArgument($"-vn {codec}"), cancellationToken);
        return [output];
    }
}

/// <summary>Bitrates and resolution that make a video of a given length fit in a given size.</summary>
public sealed record SizePlan(int VideoKbps, int AudioKbps, int Height)
{
    // Below this, H.264 can't keep a picture worth watching, whatever the resolution.
    private const int MinimumVideoKbps = 80;

    /// <param name="maxHeight">Resolution cap chosen by the user, 0 for none.</param>
    public static SizePlan For(long targetBytes, double seconds, int sourceHeight, int maxHeight, bool hasAudio)
    {
        if (seconds <= 0)
            throw new ToolException(Strings.Error_MediaUnreadable);

        // 4 % for the MP4 container and the encoder's small overshoots.
        var totalKbps = targetBytes * 8 * 0.96 / 1000 / seconds;
        var audioKbps = !hasAudio ? 0 : totalKbps switch
        {
            >= 1500 => 128,
            >= 400 => 96,
            _ => 48,
        };
        var videoKbps = (int)(totalKbps - audioKbps);
        if (videoKbps < MinimumVideoKbps)
            throw new ToolException(string.Format(Strings.Error_TargetTooSmall, Math.Ceiling(MinimumVideoKbps * seconds / 8 / 1000 / 0.96 * 1.1)));

        // Roughly what H.264 needs for a clean picture at each height.
        var height = videoKbps switch
        {
            >= 2500 => 0,
            >= 1200 => 720,
            >= 600 => 480,
            _ => 360,
        };
        if (maxHeight > 0 && (height == 0 || maxHeight < height))
            height = maxHeight;
        // Never upscale.
        if (sourceHeight > 0 && height >= sourceHeight)
            height = 0;
        return new SizePlan(videoKbps, audioKbps, height);
    }
}

/// <summary>Removes the sound of a video. Streams are copied: instant, no quality loss.</summary>
public sealed class VideoMuteTool : VideoToolBase
{
    public override string Id => "video.mute";
    public override string Name => Strings.Tool_VideoMute_Name;
    public override string Description => Strings.Tool_VideoMute_Description;
    public override bool ShowsSizeChange => false;

    public override IReadOnlyList<ToolOption> Options { get; } = [];

    public override async Task<IReadOnlyList<string>> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var input = context.Inputs[0];
        var output = await TranscodeAsync(context, OutputName(input, Path.GetExtension(input), " - " + Strings.Suffix_Muted),
            o => o.WithCustomArgument("-map 0 -map -0:a -c copy"), cancellationToken);
        return [output];
    }
}

/// <summary>
/// Brings the sound to a standard loudness (EBU R128) with ffmpeg's loudnorm in two passes: the
/// first measures, the second corrects linearly, so the dynamics are kept. Video is copied as is.
/// </summary>
public sealed class AudioNormalizeTool : VideoToolBase
{
    private static readonly IReadOnlySet<FileKind> VideoAndAudio = new HashSet<FileKind> { FileKind.Video, FileKind.Audio };

    public override string Id => "audio.normalize";
    public override string Name => Strings.Tool_AudioNormalize_Name;
    public override string Description => Strings.Tool_AudioNormalize_Description;
    public override IReadOnlySet<FileKind> AcceptedKinds => VideoAndAudio;
    public override bool ShowsSizeChange => false;

    public override IReadOnlyList<ToolOption> Options { get; } =
    [
        new ChoiceOption("target", Strings.Option_Loudness,
        [
            new Choice("-16", Strings.Choice_LoudnessVoice),
            new Choice("-14", Strings.Choice_LoudnessMusic),
            new Choice("-23", Strings.Choice_LoudnessBroadcast),
        ], "-16") { Hint = Strings.Hint_Loudness },
    ];

    public override async Task<IReadOnlyList<string>> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var input = context.Inputs[0];
        var target = context.Settings.GetInt("target", -16);
        var analysis = await AnalyseAsync(input, cancellationToken);
        if (analysis.PrimaryAudioStream is null)
            throw new ToolException(Strings.Error_NoAudio);

        // Pass 1: measure (ffmpeg prints the measures as JSON at the end of its log).
        var filter = $"loudnorm=I={target}:TP=-1.5:LRA=11";
        var (exitCode, log) = await ExternalTools.RunAsync(ExternalTools.RequireFfmpeg(),
            ["-hide_banner", "-nostats", "-i", input, "-vn", "-af", filter + ":print_format=json", "-f", "null", "-"], cancellationToken);
        var measures = Measures(log);
        if (exitCode != 0 || measures is null)
            throw new ToolException(Strings.Error_MediaConversion);
        context.Progress.Report(0.4);

        // Pass 2: correct, at a fixed sample rate (loudnorm would otherwise output 192 kHz).
        var corrected = string.Create(CultureInfo.InvariantCulture,
            $"{filter}:measured_I={measures["input_i"]}:measured_TP={measures["input_tp"]}:measured_LRA={measures["input_lra"]}:measured_thresh={measures["input_thresh"]}:offset={measures["target_offset"]}:linear=true");
        var isVideo = FileKindDetector.Detect(input) == FileKind.Video;
        var (extension, codec) = Codec(Path.GetExtension(input).ToLowerInvariant(), isVideo);
        var output = await TranscodeAsync(context, OutputName(input, extension, " - " + Strings.Suffix_Normalized), o => o.WithCustomArgument(
                $"{(isVideo ? "-map 0:v? -map 0:a:0 -c:v copy" : "-vn")} -af \"{corrected}\" -ar 48000 {codec}"),
            cancellationToken);
        return [output];
    }

    /// <summary>Output container and audio codec: the original's when possible.</summary>
    private static (string Extension, string Codec) Codec(string extension, bool isVideo) => extension switch
    {
        ".mp3" => (".mp3", "-c:a libmp3lame -b:a 192k"),
        ".flac" => (".flac", "-c:a flac"),
        ".wav" => (".wav", "-c:a pcm_s16le"),
        ".ogg" or ".opus" => (extension, "-c:a libopus -b:a 160k"),
        ".webm" => (".webm", "-c:a libopus -b:a 160k"),
        ".mkv" or ".mov" or ".m4v" or ".mp4" or ".m4a" => (extension, "-c:a aac -b:a 192k"),
        _ => (isVideo ? ".mp4" : ".m4a", "-c:a aac -b:a 192k"),
    };

    /// <summary>The measures printed by the first pass, keyed by name.</summary>
    internal static Dictionary<string, string>? Measures(string log)
    {
        var start = log.LastIndexOf('{');
        var end = log.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(log[start..(end + 1)]);
            var measures = json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
            string[] required = ["input_i", "input_tp", "input_lra", "input_thresh", "target_offset"];
            // Silence measures as "-inf": nothing to normalize.
            return required.All(k => measures.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                ? measures
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
