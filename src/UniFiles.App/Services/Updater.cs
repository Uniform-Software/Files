using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Threading;
using Velopack;
using Velopack.Sources;

namespace UniFiles.App.Services;

public enum UpdateState
{
    /// <summary>Not checked yet in this session.</summary>
    Idle,
    Checking,
    UpToDate,
    Downloading,

    /// <summary>Downloaded: installed when UniFiles quits (<see cref="Updater.ApplyOnExit"/>) or right away (<see cref="Updater.RestartNow"/>).</summary>
    Ready,
    Failed,
}

/// <summary>
/// Updates from the GitHub releases (Velopack packages). The only network access of UniFiles: it
/// asks GitHub for the latest version, then downloads it. Pre-releases are only offered to people
/// already running one. Does nothing in development builds (not packaged by Velopack).
/// </summary>
public sealed partial class Updater : ObservableObject
{
    public const string RepositoryUrl = "https://github.com/Uniform-Software/Files";

    private readonly UpdateManager? _manager;
    private UpdateInfo? _update;

    public Updater()
    {
        try
        {
            var manager = new UpdateManager(Source());
            if (manager.IsInstalled)
                _manager = manager;
        }
        catch (Exception ex)
        {
            CrashLog.Write("Updater", ex);
        }
    }

    /// <summary>GitHub releases, or UNIFILES_UPDATE_URL (a folder or a web address) to test updates locally.</summary>
    private static IUpdateSource Source() => Environment.GetEnvironmentVariable("UNIFILES_UPDATE_URL") switch
    {
        { Length: > 0 } folder when Directory.Exists(folder) => new SimpleFileSource(new DirectoryInfo(folder)),
        { Length: > 0 } url => new SimpleWebSource(url),
        _ => new GithubSource(RepositoryUrl, accessToken: null, prerelease: true),
    };

    /// <summary>False in development builds: the preferences then explain why.</summary>
    public bool IsSupported => _manager is not null;

    [ObservableProperty]
    private UpdateState _state;

    /// <summary>Version found by the last check, e.g. "0.2.1".</summary>
    [ObservableProperty]
    private string? _availableVersion;

    /// <summary>Download progress, 0 to 100.</summary>
    [ObservableProperty]
    private int _progress;

    /// <summary>Raised on the UI thread once an update is downloaded and waiting.</summary>
    public event EventHandler? UpdateReady;

    /// <summary>Checks GitHub and, if a newer version exists, downloads it. Never throws.</summary>
    public async Task CheckAsync()
    {
        if (_manager is null || State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Ready)
            return;

        State = UpdateState.Checking;
        try
        {
            var update = await _manager.CheckForUpdatesAsync();
            var current = _manager.CurrentVersion;
            // A stable version never moves to a pre-release.
            if (update is null || (current is { IsPrerelease: false } && update.TargetFullRelease.Version.IsPrerelease))
            {
                State = UpdateState.UpToDate;
                return;
            }

            AvailableVersion = update.TargetFullRelease.Version.ToString();
            Progress = 0;
            State = UpdateState.Downloading;
            await _manager.DownloadUpdatesAsync(update, percent => Dispatcher.UIThread.Post(() => Progress = percent));

            _update = update;
            State = UpdateState.Ready;
            UpdateReady?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // Offline, GitHub unreachable, rate limit: try again at the next launch.
            CrashLog.Write("Updater", ex);
            State = UpdateState.Failed;
        }
    }

    /// <summary>
    /// Called when UniFiles quits (tray "Quit", Cmd+Q, logout): the downloaded update is installed
    /// silently once the process has exited. Only one updater is ever started.
    /// </summary>
    public void ApplyOnExit()
    {
        if (_manager is null || _update is null || _applying)
            return;
        _applying = true;
        try
        {
            _manager.WaitExitThenApplyUpdates(_update.TargetFullRelease, silent: true, restart: false);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Updater", ex);
        }
    }

    /// <summary>Installs the downloaded update and restarts UniFiles (the process exits).</summary>
    public void RestartNow()
    {
        if (_manager is null || _update is null || _applying)
            return;
        _applying = true;
        _manager.ApplyUpdatesAndRestart(_update.TargetFullRelease);
    }

    private bool _applying;
}
