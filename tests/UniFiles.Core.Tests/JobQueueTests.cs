using UniFiles.Core.Jobs;
using UniFiles.Core.Storage;
using UniFiles.Core.Tools;

namespace UniFiles.Core.Tests;

public class JobQueueTests
{
    /// <summary>A tool that fails on files named "bad*" and can be slowed down.</summary>
    private sealed class FakeTool(int delayMs = 0, int parallelism = 4) : FileToolBase
    {
        public override string Id => "test.fake";
        public override ToolModule Module => ToolModule.Files;
        public override string Name => "Fake";
        public override string Description => "";
        public override IReadOnlySet<FileKind> AcceptedKinds { get; } = new HashSet<FileKind>();
        public override int MaxParallelism => parallelism;
        public override IReadOnlyList<ToolOption> Options { get; } = [];

        public override async Task<IReadOnlyList<string>> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
        {
            await Task.Delay(delayMs, cancellationToken);
            var input = context.Inputs[0];
            if (Path.GetFileName(input).StartsWith("bad"))
                throw new ToolException("broken on purpose");
            return [await WriteOutputAsync(context.Output, Path.GetFileName(input), path => File.WriteAllTextAsync(path, "ok", cancellationToken))];
        }
    }

    /// <summary>Polls a condition: timing tests must not depend on how fast the machine is.</summary>
    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not reached within 10 seconds.");
            await Task.Delay(20);
        }
    }

    private static List<string> MakeInputs(TempFolder temp, params string[] names) =>
        names.Select(n =>
        {
            var path = temp.File(n);
            File.WriteAllText(path, "input");
            return path;
        }).ToList();

    [Fact]
    public async Task A_failing_file_does_not_stop_the_batch()
    {
        using var temp = new TempFolder();
        var queue = new JobQueue();
        var inputs = MakeInputs(temp, "a.txt", "bad.txt", "c.txt");

        var job = queue.Enqueue(new FakeTool(), inputs, new ToolSettings(), new OutputFolder(temp.Sub("out")));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(JobStatus.CompletedWithErrors, job.Status);
        Assert.Equal(2, job.DoneCount);
        Assert.Equal("broken on purpose", job.Items.Single(i => i.Status == WorkStatus.Failed).Error);
    }

    [Fact]
    public async Task Retry_queues_only_the_failed_files()
    {
        using var temp = new TempFolder();
        var queue = new JobQueue();
        var job = queue.Enqueue(new FakeTool(), MakeInputs(temp, "a.txt", "bad.txt"), new ToolSettings(), new OutputFolder(temp.Sub("out")));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        var retry = queue.RetryFailed(job);

        Assert.NotNull(retry);
        Assert.Equal(["bad.txt"], retry.Inputs.Select(Path.GetFileName));
    }

    [Fact]
    public async Task Cancel_stops_the_job_and_leaves_no_placeholder()
    {
        using var temp = new TempFolder();
        var queue = new JobQueue();
        var output = temp.Sub("out");
        var inputs = MakeInputs(temp, Enumerable.Range(0, 20).Select(i => $"f{i}.txt").ToArray());

        var job = queue.Enqueue(new FakeTool(delayMs: 200, parallelism: 2), inputs, new ToolSettings(), new OutputFolder(output));
        await Task.Delay(300);
        queue.Cancel(job);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(JobStatus.Canceled, job.Status);
        Assert.True(job.DoneCount < 20);
        // Every file left in the output folder is complete: canceled work removed its placeholder.
        Assert.All(Directory.GetFiles(output), f => Assert.Equal("ok", File.ReadAllText(f)));
    }

    [Fact]
    public async Task A_compression_that_grows_the_file_keeps_the_original()
    {
        using var temp = new TempFolder();
        // Already squeezed (quality 30): recompressing at 95 can only make it heavier.
        var input = temp.File("small.jpg");
        using (var noise = NetVips.Image.Gaussnoise(400, 300, mean: 128, sigma: 40))
            noise.Cast(NetVips.Enums.BandFormat.Uchar).WriteToFile(input, new NetVips.VOption { { "Q", 30 } });
        var settings = new ToolSettings(new Dictionary<string, object?> { ["mode"] = "quality", ["quality"] = 95, ["strip"] = true });

        var job = new JobQueue().Enqueue(new UniFiles.Core.Tools.Images.ImageCompressTool(), [input], settings, new OutputFolder(temp.Sub("out")));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        var item = Assert.Single(job.Items);
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.True(item.KeptOriginal);
        Assert.Equal(1, job.KeptOriginalCount);
        var output = Assert.Single(Directory.GetFiles(temp.Sub("out")));
        Assert.Equal(TempFolder.Hash(input), TempFolder.Hash(output));
        Assert.Equal(job.InputBytes, job.OutputBytes);
    }

    [Fact]
    public async Task Pause_holds_the_next_files_until_resumed()
    {
        using var temp = new TempFolder();
        var queue = new JobQueue();
        var inputs = MakeInputs(temp, Enumerable.Range(0, 10).Select(i => $"f{i}.txt").ToArray());

        var job = queue.Enqueue(new FakeTool(delayMs: 100, parallelism: 1), inputs, new ToolSettings(), new OutputFolder(temp.Sub("out")));
        await Task.Delay(150);
        job.Pause();

        // The file already running finishes first (however slow the machine), then nothing starts.
        await WaitUntil(() => job.Items.All(i => i.Status != WorkStatus.Running));
        var doneWhilePaused = job.DoneCount;
        await Task.Delay(400);

        Assert.Equal(JobStatus.Paused, job.Status);
        Assert.Equal(doneWhilePaused, job.DoneCount);
        Assert.True(doneWhilePaused < 10);

        job.Resume();
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(10, job.DoneCount);
    }

    [Fact]
    public async Task Waiting_jobs_can_be_reordered()
    {
        using var temp = new TempFolder();
        var queue = new JobQueue(maxConcurrentJobs: 1);
        var output = new OutputFolder(temp.Sub("out"));
        var slow = queue.Enqueue(new FakeTool(delayMs: 300), MakeInputs(temp, "s.txt"), new ToolSettings(), output);
        var second = queue.Enqueue(new FakeTool(), MakeInputs(temp, "b.txt"), new ToolSettings(), output);
        var third = queue.Enqueue(new FakeTool(), MakeInputs(temp, "c.txt"), new ToolSettings(), output);

        queue.Move(third, -1);
        queue.Move(slow, +1); // running: must not move

        Assert.Equal([slow, third, second], queue.Jobs);
        await Task.WhenAll(slow.Completion, second.Completion, third.Completion).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(third.StartedAt <= second.StartedAt);
    }

    [Fact]
    public async Task Finished_jobs_are_recorded_in_the_history()
    {
        using var temp = new TempFolder();
        var history = new HistoryStore(temp.File("history.db"));
        var queue = new JobQueue();
        var recorded = new TaskCompletionSource();
        queue.JobFinished += (_, job) =>
        {
            history.Add(job);
            recorded.SetResult();
        };

        queue.Enqueue(new FakeTool(), MakeInputs(temp, "a.txt", "bad.txt"), new ToolSettings(), new OutputFolder(temp.Sub("out")));
        await recorded.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var entry = Assert.Single(history.Recent());
        Assert.Equal("test.fake", entry.ToolId);
        Assert.Equal(2, entry.FileCount);
        Assert.Equal(1, entry.FailedCount);
        Assert.Equal(JobStatus.CompletedWithErrors, entry.Status);
    }
}
