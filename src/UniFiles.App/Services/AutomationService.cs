using Avalonia.Threading;
using UniFiles.App.Platform;
using UniFiles.Core.Automation;
using UniFiles.Core.Jobs;
using UniFiles.Core.Storage;
using UniFiles.Core.Tools;

namespace UniFiles.App.Services;

/// <summary>Live state of a watched folder, shown on its card.</summary>
public sealed class AutomationStatus
{
    public bool Available { get; internal set; } = true;

    public int ProcessedToday { get; internal set; }

    public string? LastFile { get; internal set; }

    internal DateOnly Day { get; set; } = DateOnly.FromDateTime(DateTime.Now);
}

/// <summary>
/// Runs the watched folders: one <see cref="FolderWatcher"/> per enabled rule; new files are
/// queued with the rule's preset; once a file is processed successfully its original is kept,
/// moved to "Traités" or sent to the trash. Notifications are grouped: at most one every 30 s per
/// watched folder.
/// </summary>
public sealed class AutomationService : IDisposable
{
    private static readonly TimeSpan NotificationGap = TimeSpan.FromSeconds(30);

    private readonly AppServices _services;
    private readonly Dictionary<string, FolderWatcher> _watchers = [];
    private readonly Dictionary<string, AutomationStatus> _statuses = [];
    private readonly Dictionary<Job, WatchRule> _jobs = [];
    private readonly Dictionary<string, (int Files, DateTime Last, DispatcherTimer? Pending)> _notifications = [];

    public AutomationService(AppServices services)
    {
        _services = services;
        _services.Queue.JobFinished += (_, job) => Dispatcher.UIThread.Post(() => OnJobFinished(job));
    }

    /// <summary>Raised on the UI thread when a card's state changes (count, availability).</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>Shows a grouped notification: (watched folder name, files processed, module of the preset).</summary>
    public Action<string, int, UniFiles.Core.ToolModule>? Notify { get; set; }

    public AutomationStatus Status(string ruleId) =>
        _statuses.TryGetValue(ruleId, out var status) ? status : _statuses[ruleId] = new AutomationStatus();

    /// <summary>(Re)starts every enabled rule; call after any change of the rules.</summary>
    public void Refresh()
    {
        foreach (var watcher in _watchers.Values)
            watcher.Dispose();
        _watchers.Clear();

        foreach (var rule in _services.Settings.WatchRules.Where(r => r.Enabled))
        {
            if (Resolve(rule) is not var (tool, _))
                continue;
            var watcher = new FolderWatcher(rule.Folder, rule.IncludeSubfolders, tool.Accepts, path => Ignored(rule, path));
            var id = rule.Id;
            watcher.FilesReady += (_, files) => Dispatcher.UIThread.Post(() => Process(id, files));
            watcher.AvailabilityChanged += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                Status(id).Available = watcher.IsAvailable;
                StatusChanged?.Invoke(this, id);
            });
            Status(id).Available = watcher.IsAvailable;
            _watchers[id] = watcher;
        }
    }

    /// <summary>"Process the files already there", on demand.</summary>
    public int ProcessExisting(string ruleId)
    {
        if (!_watchers.TryGetValue(ruleId, out var watcher))
            return 0;
        var files = watcher.ExistingFiles();
        Process(ruleId, files);
        return files.Count;
    }

    private (IFileTool Tool, ToolSettings Settings)? Resolve(WatchRule rule) =>
        _services.Settings.Presets.FirstOrDefault(p => p.Id == rule.PresetId) is { } preset
            ? PresetResolver.Resolve(_services.Catalog, preset)
            : null;

    private OutputTarget Target(WatchRule rule) => new(
        rule.OutputMode,
        rule.OutputMode == OutputMode.Custom && !string.IsNullOrWhiteSpace(rule.CustomOutput) ? rule.CustomOutput : _services.Settings.OutputDirectory);

    /// <summary>Never pick up UniFiles' own results, nor what lands in the destination or "Traités".</summary>
    private bool Ignored(WatchRule rule, string path)
    {
        if (OutputFolder.WasWrittenByUniFiles(path))
            return true;
        var full = Path.GetFullPath(path);
        var target = Target(rule);
        return Under(full, rule.ProcessedFolder)
            || (target.Mode != OutputMode.NextToOriginals && Under(full, target.BaseDirectory));
    }

    private static bool Under(string path, string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.Ordinal);
    }

    private void Process(string ruleId, IReadOnlyList<string> files)
    {
        if (_services.Settings.WatchRules.FirstOrDefault(r => r.Id == ruleId) is not { Enabled: true } rule
            || Resolve(rule) is not var (tool, settings))
        {
            return;
        }
        var inputs = files.Where(tool.Accepts).Select(InputFile.Of).ToList();
        if (inputs.Count < tool.MinimumInputs || inputs.Count == 0)
            return;
        var job = _services.Queue.Enqueue(tool, inputs, settings, Target(rule));
        _jobs[job] = rule;
    }

    private void OnJobFinished(Job job)
    {
        if (!_jobs.Remove(job, out var rule))
            return;

        var status = Status(rule.Id);
        if (status.Day != DateOnly.FromDateTime(DateTime.Now))
        {
            status.Day = DateOnly.FromDateTime(DateTime.Now);
            status.ProcessedToday = 0;
        }

        var done = job.Items.Where(i => i.Status == WorkStatus.Done).ToList();
        foreach (var item in done)
        {
            foreach (var original in item.Inputs)
                Tidy(rule, original);
        }
        status.ProcessedToday += done.Sum(i => i.Inputs.Count);
        if (done.LastOrDefault() is { } last)
            status.LastFile = Path.GetFileName(last.Inputs[^1]);
        StatusChanged?.Invoke(this, rule.Id);

        if (done.Count > 0)
            Announce(rule, done.Sum(i => i.Inputs.Count), job.Tool.Module);
    }

    /// <summary>What happens to an original once processed.</summary>
    private static void Tidy(WatchRule rule, string original)
    {
        try
        {
            switch (rule.After)
            {
                case AfterProcessing.MoveToProcessed:
                    Directory.CreateDirectory(rule.ProcessedFolder);
                    var target = new OutputFolder(rule.ProcessedFolder).Reserve(Path.GetFileName(original));
                    File.Move(original, target, overwrite: true);
                    break;
                case AfterProcessing.Trash:
                    Trash.Move(original);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The original stays where it was; nothing is lost.
            CrashLog.Write("Automation", ex);
        }
    }

    /// <summary>One notification now, then at most one every 30 s with the files processed meanwhile.</summary>
    private void Announce(WatchRule rule, int files, UniFiles.Core.ToolModule module)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(rule.Folder));
        var (pending, last, timer) = _notifications.GetValueOrDefault(rule.Id);
        if (DateTime.UtcNow - last >= NotificationGap && timer is null)
        {
            Notify?.Invoke(name, files, module);
            _notifications[rule.Id] = (0, DateTime.UtcNow, null);
            return;
        }
        if (timer is null)
        {
            timer = new DispatcherTimer { Interval = NotificationGap - (DateTime.UtcNow - last) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var (count, _, _) = _notifications[rule.Id];
                if (count > 0)
                    Notify?.Invoke(name, count, module);
                _notifications[rule.Id] = (0, DateTime.UtcNow, null);
            };
            timer.Start();
        }
        _notifications[rule.Id] = (pending + files, last, timer);
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers.Values)
            watcher.Dispose();
        _watchers.Clear();
    }
}
