namespace UniFiles.Core.Tools;

/// <summary>Sensible defaults shared by most tools.</summary>
public abstract class FileToolBase : IFileTool
{
    public abstract string Id { get; }

    public abstract ToolModule Module { get; }

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract IReadOnlySet<FileKind> AcceptedKinds { get; }

    public virtual bool Accepts(string path) =>
        AcceptedKinds.Count == 0 || AcceptedKinds.Contains(FileKindDetector.Detect(path));

    public virtual bool CombinesInputs => false;

    public virtual int MinimumInputs => CombinesInputs ? 2 : 1;

    public virtual int MaximumInputs => int.MaxValue;

    public virtual bool NeedsEditor => false;

    public virtual bool ShowsSizeChange => true;

    public virtual bool KeepsOriginalIfLarger => false;

    public virtual int MaxParallelism => 1;

    public abstract IReadOnlyList<ToolOption> Options { get; }

    public virtual long? EstimateOutputSize(IReadOnlyList<string> inputs, ToolSettings settings) => null;

    public abstract Task<IReadOnlyList<string>> ExecuteAsync(ToolContext context, CancellationToken cancellationToken);

    protected static long TotalSize(IEnumerable<string> paths) =>
        paths.Sum(p => File.Exists(p) ? new FileInfo(p).Length : 0);

    /// <summary>Builds an output name from an input, e.g. ("C:/a/photo.png", ".webp") → "photo.webp".</summary>
    protected static string OutputName(string input, string extension, string? suffix = null) =>
        Path.GetFileNameWithoutExtension(input) + suffix + extension;

    /// <summary>
    /// Reserves an output file, runs <paramref name="write"/> on it and releases it on failure,
    /// so a failed or canceled file never leaves an empty placeholder behind.
    /// </summary>
    protected static async Task<string> WriteOutputAsync(OutputFolder output, string fileName, Func<string, Task> write)
    {
        var path = output.Reserve(fileName);
        try
        {
            await write(path);
            return path;
        }
        catch
        {
            OutputFolder.Release(path);
            throw;
        }
    }
}

/// <summary>An error whose message is already written for the end user.</summary>
public class ToolException(string message, Exception? inner = null) : Exception(message, inner);
