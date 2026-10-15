namespace UniFiles.Core.Automation;

/// <summary>Delays of a <see cref="FolderWatcher"/>; shorter in tests.</summary>
public sealed record WatcherTimings(TimeSpan Check, TimeSpan Group, TimeSpan Poll)
{
    public static WatcherTimings Default { get; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
}

/// <summary>
/// Reports the new files of a folder once they are complete. A file counts as complete when its
/// size and date stay the same between two checks and it can be opened (a download or a copy in
/// progress is never picked up). Files arriving close together are reported together. Besides
/// the system's notifications, the folder is listed regularly: network drives and sync folders
/// (iCloud, OneDrive) sometimes miss them. Files already there when watching starts are left
/// alone (see <see cref="ExistingFiles"/>).
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    private static readonly string[] TemporaryExtensions = [".part", ".crdownload", ".download", ".tmp", ".partial", ".opdownload"];

    private readonly object _sync = new();
    private readonly string _folder;
    private readonly bool _includeSubfolders;
    private readonly Func<string, bool> _accepts;
    private readonly Func<string, bool> _ignored;
    private readonly WatcherTimings _timings;
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Size, DateTime Written)> _pending = new(StringComparer.Ordinal);
    private readonly List<string> _ready = [];
    private readonly Timer _checkTimer;
    private readonly Timer _pollTimer;
    private FileSystemWatcher? _watcher;
    private DateTime _lastArrival;
    private bool _disposed;

    /// <param name="accepts">Whether a file is one the automation processes (its first action accepts it).</param>
    /// <param name="ignored">Paths never to pick up: destination folders, files UniFiles just wrote.</param>
    public FolderWatcher(string folder, bool includeSubfolders, Func<string, bool> accepts, Func<string, bool> ignored, WatcherTimings? timings = null)
    {
        _folder = Path.GetFullPath(folder);
        _includeSubfolders = includeSubfolders;
        _accepts = accepts;
        _ignored = ignored;
        _timings = timings ?? WatcherTimings.Default;
        _checkTimer = new Timer(_ => Check(), null, _timings.Check, _timings.Check);
        _pollTimer = new Timer(_ => Poll(), null, _timings.Poll, _timings.Poll);
        Start();
    }

    /// <summary>Raised (on a background thread) with complete new files, grouped.</summary>
    public event EventHandler<IReadOnlyList<string>>? FilesReady;

    /// <summary>Raised when the folder disappears or comes back (a drive unplugged, then plugged).</summary>
    public event EventHandler? AvailabilityChanged;

    public bool IsAvailable { get; private set; }

    /// <summary>Files already in the folder, for "Process the files already there".</summary>
    public IReadOnlyList<string> ExistingFiles() => IsAvailable ? List().Where(Wanted).ToList() : [];

    private void Start()
    {
        lock (_sync)
        {
            if (_disposed || !Directory.Exists(_folder))
            {
                SetAvailable(false);
                return;
            }
            foreach (var file in List())
                _known.Add(file);

            _watcher = new FileSystemWatcher(_folder)
            {
                IncludeSubdirectories = _includeSubfolders,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            };
            _watcher.Created += (_, e) => Arrived(e.FullPath);
            _watcher.Renamed += (_, e) => Arrived(e.FullPath);
            _watcher.Changed += (_, e) => Arrived(e.FullPath);
            _watcher.Error += (_, _) => Restart();
            _watcher.EnableRaisingEvents = true;
            SetAvailable(true);
        }
    }

    private void Restart()
    {
        lock (_sync)
        {
            _watcher?.Dispose();
            _watcher = null;
            _known.Clear();
        }
        Start();
    }

    private void SetAvailable(bool available)
    {
        if (IsAvailable == available)
            return;
        IsAvailable = available;
        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private IEnumerable<string> List()
    {
        try
        {
            return Directory.EnumerateFiles(_folder, "*", new EnumerationOptions
            {
                RecurseSubdirectories = _includeSubfolders,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Hidden, temporary, Office lock files and the ignored paths are never processed.</summary>
    private bool Wanted(string path)
    {
        var name = Path.GetFileName(path);
        return name.Length > 0
            && name[0] != '.'
            && !name.StartsWith("~$", StringComparison.Ordinal)
            && !TemporaryExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)
            && !_ignored(path)
            && _accepts(path);
    }

    private void Arrived(string path)
    {
        if (!Wanted(path))
            return;
        lock (_sync)
        {
            if (_known.Contains(path) || _ready.Contains(path))
                return;
            _pending.TryAdd(path, (-1, DateTime.MinValue));
            _lastArrival = DateTime.UtcNow;
        }
    }

    /// <summary>Safety net: new files the system didn't announce; also notices a folder coming back.</summary>
    private void Poll()
    {
        if (_disposed)
            return;
        if (!Directory.Exists(_folder))
        {
            Restart();
            return;
        }
        if (_watcher is null)
        {
            Start();
            return;
        }
        foreach (var file in List())
            Arrived(file);
    }

    private void Check()
    {
        List<string>? report = null;
        lock (_sync)
        {
            if (_disposed)
                return;
            foreach (var (path, previous) in _pending.ToList())
            {
                if (!File.Exists(path))
                {
                    _pending.Remove(path);
                    continue;
                }
                var info = new FileInfo(path);
                var now = (info.Length, info.LastWriteTimeUtc);
                if (now != previous || !CanOpen(path))
                {
                    _pending[path] = now;
                    continue;
                }
                _pending.Remove(path);
                _known.Add(path);
                _ready.Add(path);
            }

            // Wait for the burst of arrivals to end before reporting.
            if (_ready.Count > 0 && _pending.Count == 0 && DateTime.UtcNow - _lastArrival >= _timings.Group)
            {
                report = [.. _ready];
                _ready.Clear();
            }
        }
        if (report is not null)
            FilesReady?.Invoke(this, report);
    }

    /// <summary>False while another program still writes the file (Windows locks it; elsewhere this is a no-op).</summary>
    private static bool CanOpen(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _watcher?.Dispose();
            _watcher = null;
        }
        _checkTimer.Dispose();
        _pollTimer.Dispose();
    }
}
