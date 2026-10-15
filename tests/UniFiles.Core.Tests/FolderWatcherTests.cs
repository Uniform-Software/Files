using System.Collections.Concurrent;
using UniFiles.Core.Automation;

namespace UniFiles.Core.Tests;

public class FolderWatcherTests
{
    private static readonly WatcherTimings Fast = new(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(400));

    private sealed class Recorder : IDisposable
    {
        public readonly ConcurrentQueue<IReadOnlyList<string>> Batches = new();
        public FolderWatcher Watcher { get; }

        public Recorder(string folder, Func<string, bool>? ignored = null, Func<string, bool>? accepts = null)
        {
            Watcher = new FolderWatcher(folder, includeSubfolders: false, accepts ?? (_ => true), ignored ?? (_ => false), Fast);
            Watcher.FilesReady += (_, files) => Batches.Enqueue(files);
        }

        public IReadOnlyList<string> All => Batches.SelectMany(b => b).ToList();

        public void Dispose() => Watcher.Dispose();
    }

    private static async Task WaitUntil(Func<bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not reached in time.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task New_files_arriving_together_are_reported_once_and_together()
    {
        using var temp = new TempFolder();
        var folder = temp.Sub("watched");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "already-there.png"), "old");
        using var recorder = new Recorder(folder);

        for (var i = 0; i < 5; i++)
            File.WriteAllText(Path.Combine(folder, $"capture {i}.png"), "x");

        await WaitUntil(() => recorder.All.Count == 5);
        await Task.Delay(700);
        Assert.Single(recorder.Batches);
        Assert.DoesNotContain(recorder.All, f => f.EndsWith("already-there.png"));
        Assert.Contains(recorder.Watcher.ExistingFiles(), f => f.EndsWith("already-there.png"));
    }

    [Fact]
    public async Task A_download_is_picked_up_only_once_complete_and_renamed()
    {
        using var temp = new TempFolder();
        var folder = temp.Sub("downloads");
        Directory.CreateDirectory(folder);
        using var recorder = new Recorder(folder);

        var partial = Path.Combine(folder, "photo.jpg.crdownload");
        await using (var stream = File.Create(partial))
        {
            for (var i = 0; i < 5; i++)
            {
                await stream.WriteAsync(new byte[1000]);
                await stream.FlushAsync();
                await Task.Delay(60);
            }
        }
        await Task.Delay(500);
        Assert.Empty(recorder.All);

        File.Move(partial, Path.Combine(folder, "photo.jpg"));
        await WaitUntil(() => recorder.All.Count == 1);
        await Task.Delay(600);
        Assert.Equal(["photo.jpg"], recorder.All.Select(Path.GetFileName));
    }

    [Fact]
    public async Task Hidden_ignored_and_unaccepted_files_are_skipped()
    {
        using var temp = new TempFolder();
        var folder = temp.Sub("watched");
        var output = Path.Combine(folder, "UniFiles");
        Directory.CreateDirectory(output);
        using var recorder = new Recorder(folder,
            ignored: path => path.StartsWith(output, StringComparison.Ordinal),
            accepts: path => Path.GetExtension(path) == ".png");

        File.WriteAllText(Path.Combine(folder, ".hidden.png"), "x");
        File.WriteAllText(Path.Combine(folder, "~$lock.png"), "x");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(output, "result.png"), "x");
        File.WriteAllText(Path.Combine(folder, "good.png"), "x");

        await WaitUntil(() => recorder.All.Count == 1);
        await Task.Delay(700);
        Assert.Equal(["good.png"], recorder.All.Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_missing_folder_is_reported_and_watched_again_when_it_comes_back()
    {
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "usb");
        using var recorder = new Recorder(folder);
        Assert.False(recorder.Watcher.IsAvailable);

        Directory.CreateDirectory(folder);
        await WaitUntil(() => recorder.Watcher.IsAvailable);
        File.WriteAllText(Path.Combine(folder, "new.png"), "x");
        await WaitUntil(() => recorder.All.Count == 1);
    }
}
