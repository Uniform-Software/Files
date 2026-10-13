using System.Globalization;
using System.Text.RegularExpressions;
using UniFiles.Core.Resources;

namespace UniFiles.Core.Tools.Rename;

/// <summary>
/// Renames files from a pattern. Originals are never touched: renamed copies are written to the
/// output folder. Tokens: {name} original name, {n} counter, {date} file date, {ext} extension.
/// </summary>
public sealed partial class BatchRenameTool : FileToolBase, IPreviewableTool
{
    public override string Id => "rename.pattern";
    public override ToolModule Module => ToolModule.Rename;
    public override string Name => Strings.Tool_Rename_Name;
    public override string Description => Strings.Tool_Rename_Description;
    public override IReadOnlySet<FileKind> AcceptedKinds { get; } = new HashSet<FileKind>();
    public override int MaxParallelism => 4;
    public override bool ShowsSizeChange => false;

    public override IReadOnlyList<ToolOption> Options { get; } =
    [
        new TextOption("pattern", Strings.Option_Pattern, "{name}_{n}") { Hint = Strings.Hint_Pattern },
        new NumberOption("start", Strings.Option_CounterStart, 0, 1_000_000, 1),
        new NumberOption("digits", Strings.Option_CounterDigits, 1, 8, 3),
        new ChoiceOption("case", Strings.Option_Case,
        [
            new Choice("keep", Strings.Choice_CaseKeep),
            new Choice("lower", Strings.Choice_CaseLower),
            new Choice("upper", Strings.Choice_CaseUpper),
        ], "keep"),
    ];

    public override long? EstimateOutputSize(IReadOnlyList<string> inputs, ToolSettings settings) => TotalSize(inputs);

    public IReadOnlyList<PreviewRow> Preview(IReadOnlyList<string> inputs, ToolSettings settings) =>
        inputs.Select((input, index) => new PreviewRow(Path.GetFileName(input), NewName(input, index, settings))).ToList();

    public override async Task<IReadOnlyList<string>> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var input = context.Inputs[0];
        var output = await WriteOutputAsync(context.Output, NewName(input, context.Index, context.Settings), async path =>
        {
            await using var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await source.CopyToAsync(target, cancellationToken);
        });
        File.SetLastWriteTimeUtc(output, File.GetLastWriteTimeUtc(input));
        context.Progress.Report(1);
        return [output];
    }

    public static string NewName(string input, int index, ToolSettings settings)
    {
        var pattern = settings.GetString("pattern", "{name}_{n}");
        var counter = settings.GetInt("start", 1) + index;
        var digits = Math.Clamp(settings.GetInt("digits", 3), 1, 8);
        var extension = Path.GetExtension(input);
        var date = File.Exists(input) ? File.GetLastWriteTime(input) : DateTime.Now;

        var stem = TokenRegex().Replace(pattern, match => match.Groups[1].Value.ToLowerInvariant() switch
        {
            "name" or "nom" => Path.GetFileNameWithoutExtension(input),
            "n" => counter.ToString(new string('0', digits), CultureInfo.InvariantCulture),
            "date" => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "ext" => extension.TrimStart('.'),
            _ => match.Value,
        });

        stem = settings.GetString("case", "keep") switch
        {
            "lower" => stem.ToLowerInvariant(),
            "upper" => stem.ToUpperInvariant(),
            _ => stem,
        };

        return OutputFolder.Sanitize(stem + extension.ToLowerInvariant());
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex TokenRegex();
}
