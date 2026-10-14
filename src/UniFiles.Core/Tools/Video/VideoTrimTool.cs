using System.Globalization;
using FFMpegCore;
using UniFiles.Core.Resources;

namespace UniFiles.Core.Tools.Video;

/// <summary>A part of the video to keep, in seconds.</summary>
public readonly record struct TimeRange(double Start, double End)
{
    public double Duration => End - Start;
}

/// <summary>
/// Keeps one or several passages of a video and joins them, as a video or an animated GIF. Set up
/// in the video editor, which stores the passages in the "segments" setting ("0-12.5;20-45.2", in
/// seconds). Without it, the "start" and "end" options give a single passage.
/// </summary>
public sealed class VideoTrimTool : VideoToolBase
{
    public const string SegmentsKey = "segments";

    public override string Id => "video.trim";
    public override string Name => Strings.Tool_VideoTrim_Name;
    public override string Description => Strings.Tool_VideoTrim_Description;
    public override int MaximumInputs => 1;
    public override bool NeedsEditor => true;
    public override bool ShowsSizeChange => false;

    public override IReadOnlyList<ToolOption> Options { get; } =
    [
        new TextOption("start", Strings.Option_Start, "0:00") { Hint = Strings.Hint_Time },
        new TextOption("end", Strings.Option_End, "") { Hint = Strings.Hint_EndEmpty },
        new BoolOption("precise", Strings.Option_PreciseCut, true) { Hint = Strings.Hint_PreciseCut },
        new ChoiceOption("output", Strings.Option_ExportAs,
        [
            new Choice("video", Strings.Choice_Video),
            new Choice("gif", Strings.Choice_Gif),
        ], "video"),
        new ChoiceOption("gifWidth", Strings.Option_Width,
        [
            new Choice("320", "320 px"),
            new Choice("480", "480 px"),
            new Choice("640", "640 px"),
            new Choice("800", "800 px"),
        ], "480") { VisibleWhenKey = "output", VisibleWhenValue = "gif" },
        new ChoiceOption("gifFps", Strings.Option_Fps,
        [
            new Choice("10", string.Format(Strings.Unit_Fps, 10)),
            new Choice("15", string.Format(Strings.Unit_Fps, 15)),
            new Choice("20", string.Format(Strings.Unit_Fps, 20)),
        ], "15") { VisibleWhenKey = "output", VisibleWhenValue = "gif" },
    ];

    public static string FormatSegments(IEnumerable<TimeRange> ranges) =>
        string.Join(";", ranges.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Start:0.###}-{r.End:0.###}")));

    public static List<TimeRange> ParseSegments(string text)
    {
        var ranges = new List<TimeRange>();
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = part.Split('-');
            if (bounds.Length != 2
                || !double.TryParse(bounds[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var start)
                || !double.TryParse(bounds[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var end)
                || start < 0 || end <= start)
            {
                throw new ToolException(string.Format(Strings.Error_InvalidTime, part));
            }
            ranges.Add(new TimeRange(start, end));
        }
        if (ranges.Count == 0)
            throw new ToolException(Strings.Error_NothingKept);
        return ranges.OrderBy(r => r.Start).ToList();
    }

    public override async Task<IReadOnlyList<string>> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var input = context.Inputs[0];
        var precise = context.Settings.GetBool("precise", true);
        var segments = context.Settings.GetString(SegmentsKey);

        List<TimeRange> ranges;
        if (!string.IsNullOrWhiteSpace(segments))
        {
            ranges = ParseSegments(segments);
        }
        else
        {
            var start = ParseTime(context.Settings.GetString("start"));
            var endText = context.Settings.GetString("end");
            var end = string.IsNullOrWhiteSpace(endText) ? TimeSpan.MaxValue : ParseTime(endText);
            if (end <= start)
                throw new ToolException(Strings.Error_EndBeforeStart);
            ranges = [new TimeRange(start.TotalSeconds, end == TimeSpan.MaxValue ? double.PositiveInfinity : end.TotalSeconds)];
        }

        if (context.Settings.GetString("output", "video") == "gif")
            return [await MakeGifAsync(context, input, ranges, cancellationToken)];

        return ranges.Count == 1
            ? [await KeepOneAsync(context, input, ranges[0], precise, cancellationToken)]
            : [await KeepSeveralAsync(context, input, ranges, cancellationToken)];
    }

    /// <summary>
    /// Animated GIF of the kept passages, with a palette computed for this clip (much cleaner than
    /// the default 256 web colors), never wider than the source.
    /// </summary>
    private static async Task<string> MakeGifAsync(ToolContext context, string input, List<TimeRange> ranges, CancellationToken cancellationToken)
    {
        var analysis = await AnalyseAsync(input, cancellationToken);
        var duration = analysis.Duration.TotalSeconds;
        var width = context.Settings.GetInt("gifWidth", 480);
        var fps = context.Settings.GetInt("gifFps", 15);

        var graph = new List<string>();
        var joined = "";
        for (var i = 0; i < ranges.Count; i++)
        {
            var end = double.IsInfinity(ranges[i].End) ? "" : $":end={Seconds(ranges[i].End)}";
            graph.Add($"[0:v]trim=start={Seconds(ranges[i].Start)}{end},setpts=PTS-STARTPTS[v{i}]");
            joined += $"[v{i}]";
        }
        graph.Add($"{joined}concat=n={ranges.Count}:v=1:a=0,fps={fps},scale='min({width},iw)':-1:flags=lanczos,split[s0][s1]");
        graph.Add("[s0]palettegen=stats_mode=diff[p]");
        graph.Add("[s1][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle[g]");

        var expected = TimeSpan.FromSeconds(ranges.Sum(r => Math.Min(r.End, duration) - r.Start));
        return await TranscodeAsync(context, OutputName(input, ".gif"),
            o => o.WithCustomArgument($"-filter_complex \"{string.Join(";", graph)}\" -map [g] -an -loop 0"),
            cancellationToken,
            expectedDuration: expected);
    }

    private static Task<string> KeepOneAsync(ToolContext context, string input, TimeRange range, bool precise, CancellationToken cancellationToken)
    {
        var start = TimeSpan.FromSeconds(range.Start);
        TimeSpan? length = double.IsInfinity(range.End) ? null : TimeSpan.FromSeconds(range.Duration);

        // Copying streams is instant but can only cut on keyframes; re-encoding is exact (MP4 output).
        var extension = precise ? ".mp4" : Path.GetExtension(input);
        var name = OutputName(input, extension, " - " + Strings.Suffix_Trimmed);
        var limit = length is { } l ? $"-t {FormatTime(l)} " : "";
        Action<FFMpegArgumentOptions>? seek = start > TimeSpan.Zero ? i => i.WithCustomArgument($"-ss {FormatTime(start)}") : null;
        return precise
            ? TranscodeH264Async(context, name, 18, video => $"{limit}{video} -c:a aac -b:a 192k -movflags +faststart",
                cancellationToken, seek, length)
            : TranscodeAsync(context, name, o => o.WithCustomArgument($"{limit}-c copy -avoid_negative_ts make_zero"),
                cancellationToken, seek, length);
    }

    private static async Task<string> KeepSeveralAsync(ToolContext context, string input, List<TimeRange> ranges, CancellationToken cancellationToken)
    {
        IMediaAnalysis analysis;
        try
        {
            analysis = await FFProbe.AnalyseAsync(input, FfOptions(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ToolException)
        {
            throw new ToolException(Strings.Error_MediaUnreadable, ex);
        }
        var hasAudio = analysis.AudioStreams.Count > 0;

        // [0:v]trim=start=a:end=b,setpts=PTS-STARTPTS[v0]; ... [v0][a0][v1][a1]concat=n=2:v=1:a=1[v][a]
        var graph = new List<string>();
        var inputs = "";
        for (var i = 0; i < ranges.Count; i++)
        {
            var (start, end) = (Seconds(ranges[i].Start), Seconds(ranges[i].End));
            graph.Add($"[0:v]trim=start={start}:end={end},setpts=PTS-STARTPTS[v{i}]");
            inputs += $"[v{i}]";
            if (hasAudio)
            {
                graph.Add($"[0:a]atrim=start={start}:end={end},asetpts=PTS-STARTPTS[a{i}]");
                inputs += $"[a{i}]";
            }
        }
        graph.Add(hasAudio
            ? $"{inputs}concat=n={ranges.Count}:v=1:a=1[v][a]"
            : $"{inputs}concat=n={ranges.Count}:v=1:a=0[v]");

        var maps = hasAudio ? "-map [v] -map [a]" : "-map [v]";
        var expected = TimeSpan.FromSeconds(ranges.Sum(r => Math.Min(r.End, analysis.Duration.TotalSeconds) - r.Start));

        return await TranscodeH264Async(context, OutputName(input, ".mp4", " - " + Strings.Suffix_Trimmed), 18,
            video => $"-filter_complex \"{string.Join(";", graph)}\" {maps} {video} -c:a aac -b:a 192k -movflags +faststart",
            cancellationToken,
            expectedDuration: expected);
    }

    private static string Seconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
