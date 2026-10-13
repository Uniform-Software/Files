using UniFiles.Core.Tools;

namespace UniFiles.Core.Jobs;

/// <summary>
/// Runs jobs in the background, off the UI thread. Several jobs run at once; within a job,
/// files are processed in parallel up to <see cref="IFileTool.MaxParallelism"/>.
/// A file that fails is reported and the rest of the batch carries on.
/// </summary>
public sealed class JobQueue
{
    private readonly object _sync = new();
    private readonly List<Job> _jobs = [];
    private readonly int _maxConcurrentJobs;
    private int _running;

    public JobQueue(int maxConcurrentJobs = 2)
    {
        _maxConcurrentJobs = Math.Max(1, maxConcurrentJobs);
    }

    /// <summary>Raised when a job is added, removed or moved.</summary>
    public event EventHandler? JobsChanged;

    /// <summary>Raised once per job, when it reaches a final status.</summary>
    public event EventHandler<Job>? JobFinished;

    public IReadOnlyList<Job> Jobs
    {
        get { lock (_sync) return _jobs.ToList(); }
    }

    public Job Enqueue(IFileTool tool, IReadOnlyList<string> inputs, ToolSettings settings, OutputFolder output)
    {
        if (inputs.Count < tool.MinimumInputs)
            throw new ArgumentException($"{tool.Id} needs at least {tool.MinimumInputs} files.", nameof(inputs));

        var job = new Job(tool, inputs.ToList(), settings.Clone(), output);
        // A waiting job that gets resumed must be able to start without another event.
        job.Changed += (_, _) =>
        {
            if (job.Status == JobStatus.Pending && job.StartedAt is null)
                StartNext();
        };
        lock (_sync)
            _jobs.Add(job);
        JobsChanged?.Invoke(this, EventArgs.Empty);
        StartNext();
        return job;
    }

    /// <summary>Queues a new job with the files that failed or were canceled in <paramref name="job"/>.</summary>
    public Job? RetryFailed(Job job)
    {
        var inputs = job.Items
            .Where(i => i.Status is WorkStatus.Failed or WorkStatus.Canceled)
            .SelectMany(i => i.Inputs)
            .ToList();
        return inputs.Count >= job.Tool.MinimumInputs ? Enqueue(job.Tool, inputs, job.Settings, job.Output) : null;
    }

    /// <summary>Moves a waiting job one place up (-1) or down (+1) among waiting jobs.</summary>
    public void Move(Job job, int direction)
    {
        lock (_sync)
        {
            var index = _jobs.IndexOf(job);
            var target = index + Math.Sign(direction);
            if (index < 0 || target < 0 || target >= _jobs.Count)
                return;
            if (job.StartedAt is not null || _jobs[target].StartedAt is not null)
                return;
            (_jobs[index], _jobs[target]) = (_jobs[target], _jobs[index]);
        }
        JobsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes finished jobs from the list.</summary>
    public void ClearFinished()
    {
        lock (_sync)
            _jobs.RemoveAll(j => j.IsFinished);
        JobsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Cancel(Job job)
    {
        job.Cancel();
        // A job that never started finishes right away; a running one stops in its worker.
        lock (_sync)
        {
            if (job.StartedAt is not null || job.IsFinished)
                return;
            foreach (var item in job.Items)
                item.Status = WorkStatus.Canceled;
            // Inside the lock so StartNext cannot pick it up in between.
            job.SetStatus(JobStatus.Canceled);
        }
        JobFinished?.Invoke(this, job);
    }

    private void StartNext()
    {
        while (true)
        {
            Job? next;
            lock (_sync)
            {
                if (_running >= _maxConcurrentJobs)
                    return;
                next = _jobs.FirstOrDefault(j => j.Status == JobStatus.Pending && j.StartedAt is null);
                if (next is null)
                    return;
                next.StartedAt = DateTime.Now;
                _running++;
            }
            _ = Task.Run(() => RunAsync(next));
        }
    }

    private async Task RunAsync(Job job)
    {
        try
        {
            if (job.Status != JobStatus.Paused)
                job.SetStatus(JobStatus.Running);

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, job.Tool.MaxParallelism),
                CancellationToken = job.CancellationToken,
            };
            try
            {
                await Parallel.ForEachAsync(job.Items, options, (item, ct) => ProcessAsync(job, item, ct));
            }
            catch (OperationCanceledException) when (job.CancellationToken.IsCancellationRequested)
            {
                // Canceled by the user: handled below.
            }

            foreach (var item in job.Items.Where(i => i.Status is WorkStatus.Pending or WorkStatus.Running))
                item.Status = WorkStatus.Canceled;

            job.SetStatus(job.CancellationToken.IsCancellationRequested ? JobStatus.Canceled
                : job.FailedCount == job.Items.Count ? JobStatus.Failed
                : job.FailedCount > 0 ? JobStatus.CompletedWithErrors
                : JobStatus.Completed);
        }
        catch (Exception)
        {
            // Should never happen, but a job must never stay "running" forever.
            job.SetStatus(JobStatus.Failed);
        }
        finally
        {
            lock (_sync)
                _running--;
            JobFinished?.Invoke(this, job);
            StartNext();
        }
    }

    private static async ValueTask ProcessAsync(Job job, WorkItem item, CancellationToken cancellationToken)
    {
        await job.Gate.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        item.Status = WorkStatus.Running;
        item.InputBytes = item.Inputs.Sum(SizeOf);
        job.RaiseChanged();

        var context = new ToolContext
        {
            Inputs = item.Inputs,
            Index = item.Index,
            Settings = job.Settings,
            Output = job.Output,
            Progress = new SyncProgress(value =>
            {
                item.Progress = value;
                job.RaiseChanged();
            }),
        };

        try
        {
            item.Outputs = await job.Tool.ExecuteAsync(context, cancellationToken);
            item.OutputBytes = item.Outputs.Sum(SizeOf);
            item.Status = WorkStatus.Done;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            item.Status = WorkStatus.Canceled;
        }
        catch (Exception ex)
        {
            item.Error = ErrorDescriber.Describe(ex);
            item.Status = WorkStatus.Failed;
        }
        item.Progress = 1;
        job.RaiseChanged();
    }

    private static long SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>Reports synchronously, unlike <see cref="Progress{T}"/> which posts and may reorder updates.</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
