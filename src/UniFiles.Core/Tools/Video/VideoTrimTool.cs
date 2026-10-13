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
/// Keeps one or several passages of a video and joins them. Set up in the video editor, which
/// stores the passages in the "segments" setting ("0-12.5;20-45.2", in seconds). Without it,
/// the "start" and "end" options give a single passage.
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

        return ranges.Count == 1
            ? [await KeepOneAsync(context, input, ranges[0], precise, cancellationToken)]
            : [await KeepSeveralAsync(context, input, ranges, cancellationToken)];
    }

    private static Task<string> KeepOneAsync(ToolContext context, string input, TimeRange range, bool precise, CancellationToken cancellationToken)
    {
        var start = TimeSpan.FromSeconds(range.Start);
        TimeSpan? length = double.IsInfinity(range.End) ? null : TimeSpan.FromSeconds(range.Duration);

        // Copying streams is instant but can only cut on keyframes; re-encoding is exact (MP4 output).
        var extension = precise ? ".mp4" : Path.GetExtension(input);
        return TranscodeAsync(context, OutputName(input, extension, " - " + Strings.Suffix_Trimmed),
            o =>
            {
                if (length is { } l)
                    o.WithCustomArgument($"-t {FormatTime(l)}");
                o.WithCustomArgument(precise
                    ? "-c:v libx264 -crf 18 -preset fast -pix_fmt yuv420p -c:a aac -b:a 192k -movflags +faststart"
                    : "-c copy -avoid_negative_ts make_zero");
            },
            cancellationToken,
            inputOptions: start > TimeSpan.Zero ? i => i.WithCustomArgument($"-ss {FormatTime(start)}") : null,
            expectedDuration: length);
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

        return await TranscodeAsync(context, OutputName(input, ".mp4", " - " + Strings.Suffix_Trimmed),
            o => o.WithCustomArgument(
                $"-filter_complex \"{string.Join(";", graph)}\" {maps} " +
                "-c:v libx264 -crf 18 -preset fast -pix_fmt yuv420p -c:a aac -b:a 192k -movflags +faststart"),
            cancellationToken,
            expectedDuration: expected);
    }

    private static string Seconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
