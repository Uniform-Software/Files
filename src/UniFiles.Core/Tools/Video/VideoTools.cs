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
        var options = FfOptions();

        TimeSpan duration;
        try
        {
            duration = expectedDuration ?? (await FFProbe.AnalyseAsync(input, options, cancellationToken)).Duration;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ToolException)
        {
            throw new ToolException(Strings.Error_MediaUnreadable, ex);
        }

        return await WriteOutputAsync(context.Output, outputName, async path =>
        {
            try
            {
                await FFMpegArguments
                    .FromFileInput(input, verifyExists: true, inputOptions)
                    .OutputToFile(path, overwrite: true, outputOptions)
                    .NotifyOnProgress(percent => context.Progress.Report(Math.Clamp(percent / 100.0, 0, 1)), duration)
                    .CancellableThrough(cancellationToken)
                    .ProcessAsynchronously(throwOnError: true, options);
            }
            catch (Exception ex) when (ex is FFMpegException or FFMpegArgumentException && !cancellationToken.IsCancellationRequested)
            {
                throw new ToolException(Strings.Error_MediaConversion, ex);
            }
            cancellationToken.ThrowIfCancellationRequested();
        });
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
    public override string Name => Strings.Tool_VideoCompress_Name;
    public override string Description => Strings.Tool_VideoCompress_Description;

    public override IReadOnlyList<ToolOption> Options { get; } =
    [
        new ChoiceOption("quality", Strings.Option_Quality,
        [
            new Choice("high", Strings.Choice_QualityHigh),
            new Choice("balanced", Strings.Choice_QualityBalanced),
            new Choice("small", Strings.Choice_QualitySmall),
        ], "balanced"),
        new ChoiceOption("maxHeight", Strings.Option_Resolution,
        [
            new Choice("0", Strings.Choice_KeepResolution),
            new Choice("1080", "1080p"),
            new Choice("720", "720p"),
            new Choice("480", "480p"),
        ], "0"),
    ];

    public override long? EstimateOutputSize(IReadOnlyList<string> inputs, ToolSettings settings)
    {
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
        var input = context.Inputs[0];
        var crf = context.Settings.GetString("quality", "balanced") switch
        {
            "high" => 23,
            "small" => 32,
            _ => 28,
        };
        var maxHeight = context.Settings.GetInt("maxHeight");

        var output = await TranscodeAsync(context, OutputName(input, ".mp4", " - " + Strings.Suffix_Compressed), o =>
        {
            if (maxHeight > 0)
                o.WithCustomArgument($"-vf scale=-2:'trunc(min({maxHeight},ih)/2)*2'");
            o.WithCustomArgument($"-c:v libx264 -crf {crf} -preset medium -pix_fmt yuv420p -c:a aac -b:a 128k -movflags +faststart");
        }, cancellationToken);

        return [output];
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
        var arguments = format switch
        {
            "webm" => "-c:v libvpx-vp9 -crf 32 -b:v 0 -row-mt 1 -c:a libopus -b:a 128k",
            // MKV accepts almost any codec: copy the streams, no quality loss.
            "mkv" => "-map 0 -c copy",
            _ => "-c:v libx264 -crf 20 -preset medium -pix_fmt yuv420p -c:a aac -b:a 192k -movflags +faststart",
        };

        var output = await TranscodeAsync(context, OutputName(input, "." + format), o => o.WithCustomArgument(arguments), cancellationToken);
        return [output];
    }
}

public sealed class AudioExtractTool : VideoToolBase
{
    private static readonly IReadOnlySet<FileKind> VideoAndAudio = new HashSet<FileKind> { FileKind.Video, FileKind.Audio };

    public override string Id => "audio.extract";
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
