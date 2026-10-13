using UniFiles.Core.Tools;

namespace UniFiles.Core.Jobs;

public enum JobStatus
{
    Pending,
    Running,
    Paused,
    Completed,
    CompletedWithErrors,
    Failed,
    Canceled,
}

public enum WorkStatus
{
    Pending,
    Running,
    Done,
    Failed,
    Canceled,
}

/// <summary>
/// One unit of work: a single file for most tools, or every file for tools that combine inputs.
/// </summary>
public sealed class WorkItem
{
    internal WorkItem(int index, IReadOnlyList<string> inputs)
    {
        Index = index;
        Inputs = inputs;
    }

    public int Index { get; }

    public IReadOnlyList<string> Inputs { get; }

    public string DisplayName => Inputs.Count == 1
        ? Path.GetFileName(Inputs[0])
        : $"{Path.GetFileName(Inputs[0])} + {Inputs.Count - 1}";

    public WorkStatus Status { get; internal set; }

    public double Progress { get; internal set; }

    public IReadOnlyList<string> Outputs { get; internal set; } = [];

    /// <summary>Plain-language explanation when <see cref="Status"/> is Failed.</summary>
    public string? Error { get; internal set; }

    public long InputBytes { get; internal set; }

    public long OutputBytes { get; internal set; }
}

/// <summary>A tool applied to a set of files, as shown in the queue.</summary>
public sealed class Job
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Job(IFileTool tool, IReadOnlyList<string> inputs, ToolSettings settings, OutputFolder output)
    {
        Tool = tool;
        Inputs = inputs;
        Settings = settings;
        Output = output;
        Items = tool.CombinesInputs
            ? [new WorkItem(0, inputs)]
            : inputs.Select((input, index) => new WorkItem(index, [input])).ToList();
    }

    public Guid Id { get; } = Guid.NewGuid();

    public IFileTool Tool { get; }

    public IReadOnlyList<string> Inputs { get; }

    public ToolSettings Settings { get; }

    public OutputFolder Output { get; }

    public IReadOnlyList<WorkItem> Items { get; }

    public DateTime CreatedAt { get; } = DateTime.Now;

    public DateTime? StartedAt { get; internal set; }

    public DateTime? FinishedAt { get; internal set; }

    public JobStatus Status { get; private set; }

    public bool IsFinished => Status is JobStatus.Completed or JobStatus.CompletedWithErrors or JobStatus.Failed or JobStatus.Canceled;

    public double Progress => Items.Count == 0 ? 0 : Items.Sum(i => i.Status is WorkStatus.Pending or WorkStatus.Running ? i.Progress : 1) / Items.Count;

    public int DoneCount => Items.Count(i => i.Status == WorkStatus.Done);

    public int FailedCount => Items.Count(i => i.Status == WorkStatus.Failed);

    public string? CurrentItem => Items.FirstOrDefault(i => i.Status == WorkStatus.Running)?.DisplayName;

    /// <summary>Size of the inputs that were processed successfully.</summary>
    public long InputBytes => Items.Where(i => i.Status == WorkStatus.Done).Sum(i => i.InputBytes);

    public long OutputBytes => Items.Where(i => i.Status == WorkStatus.Done).Sum(i => i.OutputBytes);

    /// <summary>Completes when the job reaches a final status.</summary>
    public Task Completion => _completion.Task;

    internal PauseGate Gate { get; } = new();

    internal CancellationToken CancellationToken => _cancellation.Token;

    /// <summary>Raised from worker threads whenever status or progress changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Pauses before the next file; files already running finish first.</summary>
    public void Pause()
    {
        if (Status is not (JobStatus.Running or JobStatus.Pending))
            return;
        Gate.Close();
        SetStatus(JobStatus.Paused);
    }

    public void Resume()
    {
        if (Status != JobStatus.Paused)
            return;
        Gate.Open();
        SetStatus(StartedAt is null ? JobStatus.Pending : JobStatus.Running);
    }

    public void Cancel()
    {
        if (IsFinished)
            return;
        _cancellation.Cancel();
        Gate.Open();
    }

    internal void SetStatus(JobStatus status)
    {
        Status = status;
        if (IsFinished)
        {
            FinishedAt ??= DateTime.Now;
            _completion.TrySetResult();
        }
        RaiseChanged();
    }

    internal void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>An async gate: open lets work through, closed makes it wait.</summary>
internal sealed class PauseGate
{
    private volatile TaskCompletionSource _signal = CreateOpen();

    public bool IsOpen => _signal.Task.IsCompleted;

    public void Open() => _signal.TrySetResult();

    public void Close()
    {
        if (IsOpen)
            _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Task WaitAsync(CancellationToken cancellationToken) => _signal.Task.WaitAsync(cancellationToken);

    private static TaskCompletionSource CreateOpen()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
