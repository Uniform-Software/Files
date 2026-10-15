using UniFiles.Core.Resources;

namespace UniFiles.Core.Tools;

/// <summary>One action of a chain, with its own settings.</summary>
public sealed record ChainStep(IFileTool Tool, ToolSettings Settings);

/// <summary>
/// Several actions one after the other on each file ("resize → convert → remove metadata"),
/// seen by the queue as a single tool. Every result of a step is an input of the next one (PDF to
/// images can multiply the files); only the last step writes to the destination, the others work
/// in a temporary folder deleted at the end. A step combining files (merge, ZIP) can only be last.
/// </summary>
public sealed class ChainTool : FileToolBase
{
    public const string ChainId = "chain";

    public ChainTool(IReadOnlyList<ChainStep> steps)
    {
        if (Validate(steps.Select(s => s.Tool).ToList()) is { } problem)
            throw new ArgumentException(problem, nameof(steps));
        Steps = steps;
    }

    public IReadOnlyList<ChainStep> Steps { get; }

    public override string Id => ChainId;

    public override ToolModule Module => Steps[0].Tool.Module;

    public override string Name => string.Join(" → ", Steps.Select(s => s.Tool.Name));

    public override string Description => string.Format(Strings.Chain_Steps, Steps.Count);

    public override IReadOnlySet<FileKind> AcceptedKinds => Steps[0].Tool.AcceptedKinds;

    public override bool Accepts(string path) => Steps[0].Tool.Accepts(path);

    public override FileKind OutputKind(FileKind input) => Steps.Aggregate(input, (kind, step) => step.Tool.OutputKind(kind));

    public override bool CombinesInputs => Steps[^1].Tool.CombinesInputs;

    public override int MinimumInputs => CombinesInputs ? 1 : base.MinimumInputs;

    public override bool ShowsSizeChange => Steps.Any(s => s.Tool.ShowsSizeChange);

    // The original is kept only if every step is a compression that may make it heavier.
    public override bool KeepsOriginalIfLarger => Steps.All(s => s.Tool.KeepsOriginalIfLarger);

    public override int MaxParallelism => Steps.Min(s => s.Tool.MaxParallelism);

    // Settings live in the steps; the chain's own settings only hold its destination.
    public override IReadOnlyList<ToolOption> Options { get; } = [];

    /// <summary>Null when these tools can be chained in this order, else what's wrong, for the user.</summary>
    public static string? Validate(IReadOnlyList<IFileTool> tools, FileKind? inputKind = null)
    {
        if (tools.Count < 2)
            return Strings.Chain_NotAllowed;
        for (var i = 0; i < tools.Count; i++)
        {
            var tool = tools[i];
            if (tool.NeedsEditor || tool is ChainTool || (tool.CombinesInputs && i < tools.Count - 1))
                return string.Format(Strings.Chain_StepNotAllowed, tool.Name);
        }

        // What each step produces must be accepted by the next one.
        var kind = inputKind ?? tools[0].AcceptedKinds.FirstOrDefault(FileKind.Unknown);
        for (var i = 1; i < tools.Count; i++)
        {
            kind = tools[i - 1].OutputKind(kind);
            if (tools[i].AcceptedKinds.Count > 0 && !tools[i].AcceptedKinds.Contains(kind))
                return string.Format(Strings.Chain_Incompatible, tools[i].Name, tools[i - 1].Name);
        }
        return null;
    }

    public override async Task<IReadOnlyList<string>> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"unifiles-chain-{Guid.NewGuid():N}");
        try
        {
            IReadOnlyList<InputFile> current = context.Files;
            for (var i = 0; i < Steps.Count; i++)
            {
                var (tool, settings) = (Steps[i].Tool, Steps[i].Settings);
                var output = i == Steps.Count - 1 ? context.Output : new OutputFolder(Path.Combine(temp, i.ToString()));
                var stepIndex = i;
                void Report(double done) => context.Progress.Report((stepIndex + Math.Clamp(done, 0, 1)) / Steps.Count);

                try
                {
                    current = tool.CombinesInputs
                        ? await RunAsync(tool, settings, current, output, context.Index, Report, cancellationToken)
                        : await RunEachAsync(tool, settings, current, output, context.Index, Report, cancellationToken);
                }
                catch (ToolException ex)
                {
                    throw new ToolException(string.Format(Strings.Chain_StepError, i + 1, tool.Name, ex.Message), ex);
                }
            }
            return current.Select(f => f.Path).ToList();
        }
        finally
        {
            try
            {
                if (Directory.Exists(temp))
                    Directory.Delete(temp, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file still held by a canceled program: the system cleans its temp folder.
            }
        }
    }

    private static async Task<IReadOnlyList<InputFile>> RunAsync(IFileTool tool, ToolSettings settings, IReadOnlyList<InputFile> files,
        OutputFolder output, int index, Action<double> report, CancellationToken cancellationToken)
    {
        var results = await tool.ExecuteAsync(new ToolContext
        {
            Inputs = files.Select(f => f.Path).ToList(),
            Files = files,
            Index = index,
            Settings = settings,
            Output = output,
            Progress = new Progress(report),
        }, cancellationToken);
        return results.Select(InputFile.Of).ToList();
    }

    /// <summary>Runs a per-file tool on each file; results keep their file's place (for archives at the end).</summary>
    private static async Task<IReadOnlyList<InputFile>> RunEachAsync(IFileTool tool, ToolSettings settings, IReadOnlyList<InputFile> files,
        OutputFolder output, int index, Action<double> report, CancellationToken cancellationToken)
    {
        var results = new List<InputFile>();
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            if (!tool.Accepts(file.Path))
                throw new ToolException(string.Format(Strings.Chain_FileNotAccepted, Path.GetFileName(file.Path), tool.Name));
            var done = i;
            var outputs = await tool.ExecuteAsync(new ToolContext
            {
                Inputs = [file.Path],
                Files = [file],
                Index = index + i,
                Settings = settings,
                Output = output,
                Progress = new Progress(p => report((done + p) / files.Count)),
            }, cancellationToken);
            results.AddRange(outputs.Select(o => file with { Path = o }));
        }
        return results;
    }

    /// <summary>Reports synchronously (the queue already marshals progress to the UI).</summary>
    private sealed class Progress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
