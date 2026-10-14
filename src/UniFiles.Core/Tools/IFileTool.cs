namespace UniFiles.Core.Tools;

/// <summary>
/// A processing action (compress images, merge PDFs...). Adding a tool means writing one class
/// implementing this interface and registering it in <see cref="ToolCatalog"/>: the UI builds its
/// settings panel from <see cref="Options"/> and the queue runs it through <see cref="ExecuteAsync"/>.
/// </summary>
public interface IFileTool
{
    /// <summary>Stable identifier, stored in the history.</summary>
    string Id { get; }

    ToolModule Module { get; }

    string Name { get; }

    string Description { get; }

    /// <summary>Kinds of files this tool accepts. Empty means any file.</summary>
    IReadOnlySet<FileKind> AcceptedKinds { get; }

    /// <summary>Whether this file can be processed: its kind, and for some tools its exact format.</summary>
    bool Accepts(string path);

    /// <summary>
    /// True when all inputs produce a single result (merge, images to PDF).
    /// False when each input is processed on its own.
    /// </summary>
    bool CombinesInputs { get; }

    /// <summary>Minimum number of input files.</summary>
    int MinimumInputs { get; }

    /// <summary>Maximum number of input files (the video editor works on one file).</summary>
    int MaximumInputs { get; }

    /// <summary>
    /// True when the tool is set up in a dedicated editor rather than the generic settings panel.
    /// Such tools are not offered as right-click quick actions.
    /// </summary>
    bool NeedsEditor { get; }

    /// <summary>
    /// Whether "12 MB → 3 MB (−75 %)" is meaningful for this tool. False when the size is not the
    /// point (renaming, merging, cutting a video).
    /// </summary>
    bool ShowsSizeChange { get; }

    /// <summary>
    /// For tools whose only purpose is a smaller file (compression): when the result is heavier than
    /// the original, the original is copied to the output instead.
    /// </summary>
    bool KeepsOriginalIfLarger { get; }

    /// <summary>How many files of the same job may be processed at once.</summary>
    int MaxParallelism { get; }

    IReadOnlyList<ToolOption> Options { get; }

    /// <summary>Rough output size, shown before launching. Null when no sensible guess exists.</summary>
    long? EstimateOutputSize(IReadOnlyList<string> inputs, ToolSettings settings);

    /// <summary>
    /// Processes <see cref="ToolContext.Inputs"/>: a single file for per-file tools,
    /// every file for tools that combine inputs. Returns the files written.
    /// </summary>
    Task<IReadOnlyList<string>> ExecuteAsync(ToolContext context, CancellationToken cancellationToken);
}

/// <summary>A tool able to show its result before running (e.g. new file names).</summary>
public interface IPreviewableTool
{
    IReadOnlyList<PreviewRow> Preview(IReadOnlyList<string> inputs, ToolSettings settings);
}

public sealed record PreviewRow(string Before, string After);

/// <summary>Everything a tool needs for one execution.</summary>
public sealed class ToolContext
{
    public required IReadOnlyList<string> Inputs { get; init; }

    /// <summary>Position of the first input within the whole job (used by counters).</summary>
    public int Index { get; init; }

    public required ToolSettings Settings { get; init; }

    public required OutputFolder Output { get; init; }

    /// <summary>Progress of this execution, from 0 to 1.</summary>
    public IProgress<double> Progress { get; init; } = new Progress<double>();
}
