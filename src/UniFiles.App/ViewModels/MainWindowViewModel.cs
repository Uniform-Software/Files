using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniFiles.App.Platform;
using UniFiles.App.Services;
using UniFiles.Core;
using UniFiles.Core.Jobs;
using UniFiles.Core.Resources;
using UniFiles.Core.Tools.Pdf;
using UniFiles.Core.Tools.Video;

namespace UniFiles.App.ViewModels;

/// <summary>Base class of the screens shown in the main area.</summary>
public abstract class PageViewModel : ObservableObject;

public sealed class NavItemViewModel(string title, string iconKey, PageViewModel page) : ObservableObject
{
    private int _badge;

    public string Title { get; } = title;

    public Geometry? Icon { get; } = ModuleInfo.Resource<Geometry>(iconKey);

    public PageViewModel Page { get; } = page;

    public int Badge
    {
        get => _badge;
        set
        {
            if (SetProperty(ref _badge, value))
                OnPropertyChanged(nameof(HasBadge));
        }
    }

    public bool HasBadge => Badge > 0;

    public override string ToString() => Title;
}

public partial class MainWindowViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _notificationTimer;
    private readonly DispatcherTimer _quickActionTimer;
    private readonly Dictionary<string, List<string>> _quickActionFiles = [];
    private string? _notificationFolder;

    public MainWindowViewModel(AppServices services)
    {
        _services = services;
        Home = new HomeViewModel(this, services);
        Queue = new QueueViewModel(services);
        History = new HistoryViewModel(services);
        Preferences = new PreferencesViewModel(this, services);

        NavItems =
        [
            new NavItemViewModel(Strings.Nav_Home, "IconHome", Home),
            new NavItemViewModel(Strings.Nav_Queue, "IconQueue", Queue),
            new NavItemViewModel(Strings.Nav_History, "IconHistory", History),
            new NavItemViewModel(Strings.Nav_Preferences, "IconSettings", Preferences),
        ];
        _selectedNav = NavItems[0];
        _currentPage = Home;
        _reducedMotion = services.Settings.ReduceMotion || Motion.SystemPrefersReducedMotion();

        _notificationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _notificationTimer.Tick += (_, _) => DismissNotification();

        // The right-click menu starts one process per selected file: gather them for a moment.
        _quickActionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _quickActionTimer.Tick += (_, _) => RunQuickActions();

        Updater = services.Updater;
        Updater.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowUpdateButton));
            OnPropertyChanged(nameof(UpdateButtonText));
        };

        services.Queue.JobsChanged += (_, _) => Dispatcher.UIThread.Post(UpdateQueueBadge);
        services.Queue.JobFinished += (_, job) => Dispatcher.UIThread.Post(() => OnJobFinished(job));
    }

    public HomeViewModel Home { get; }

    public QueueViewModel Queue { get; }

    public HistoryViewModel History { get; }

    public PreferencesViewModel Preferences { get; }

    public ObservableCollection<NavItemViewModel> NavItems { get; }

    [ObservableProperty]
    private NavItemViewModel? _selectedNav;

    [ObservableProperty]
    private PageViewModel _currentPage;

    [ObservableProperty]
    private bool _isDragging;

    [ObservableProperty]
    private bool _reducedMotion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotification))]
    private string? _notification;

    [ObservableProperty]
    private bool _notificationIsError;

    public bool HasNotification => Notification is not null;

    /// <summary>Badge of the notification: the module's, or lemon with a check / warning sign.</summary>
    [ObservableProperty]
    private IBrush? _notificationTile;

    [ObservableProperty]
    private Geometry? _notificationIcon;

    /// <summary>Whether the main window is shown and focused; otherwise notifications go to <see cref="BackgroundNotifier"/>.</summary>
    public bool IsWindowActive { get; set; }

    public INotifier? BackgroundNotifier { get; set; }

    // ---- Update button (sidebar) ----------------------------------------------------------

    public Updater Updater { get; }

    /// <summary>Set by the app: installs the update and relaunches.</summary>
    public Action? RestartForUpdate { get; set; }

    /// <summary>The user asked for the update while files were being processed: it waits for them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateButtonText))]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    private bool _updateWhenIdle;

    public bool ShowUpdateButton => Updater.State == UpdateState.Ready;

    public string UpdateButtonText => UpdateWhenIdle
        ? Strings.Update_AfterJobsButton
        : string.Format(Strings.Update_Button, Updater.AvailableVersion);

    private bool HasActiveJobs => _services.Queue.Jobs.Any(j => !j.IsFinished);

    private bool CanInstallUpdate() => !UpdateWhenIdle;

    /// <summary>Installs now, or right after the files being processed (never interrupts them).</summary>
    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    public void InstallUpdate()
    {
        if (HasActiveJobs)
        {
            UpdateWhenIdle = true;
            ShowNotification(Strings.Update_AfterJobs);
            return;
        }
        RestartForUpdate?.Invoke();
    }

    partial void OnSelectedNavChanged(NavItemViewModel? value)
    {
        if (value is null)
            return;
        CurrentPage = value.Page;
        if (value.Page == History)
            History.Refresh();
        if (value.Page == Home)
            Home.Refresh();
    }

    /// <summary>Entry point for dropped, picked or command-line files.</summary>
    public void OpenFiles(IEnumerable<string> paths, ToolModule? module = null)
    {
        var files = FileKindDetector.ExpandPaths(paths);
        if (files.Count == 0)
        {
            ShowNotification(Strings.Notification_NoFiles, isError: true);
            return;
        }

        var action = new ActionViewModel(this, _services, files, module);
        if (action.Tools.Count == 0)
        {
            ShowNotification(Strings.Notification_NoTool, isError: true);
            return;
        }

        SelectedNav = NavItems[0];
        CurrentPage = action;
    }

    /// <summary>Leaving a page that holds resources (the video player) releases them.</summary>
    partial void OnCurrentPageChanged(PageViewModel? oldValue, PageViewModel newValue)
    {
        if (oldValue is IDisposable disposable && !ReferenceEquals(oldValue, newValue))
            disposable.Dispose();
    }

    public void ShowPage(PageViewModel page)
    {
        SelectedNav = NavItems[0];
        CurrentPage = page;
    }

    public void OpenTrimEditor(VideoTrimTool tool, string file, PageViewModel back) =>
        ShowPage(new TrimEditorViewModel(this, _services, tool, file, back));

    public void OpenPdfOrganizer(PdfOrganizeTool tool, string file, PageViewModel back) =>
        ShowPage(new PdfOrganizerViewModel(this, _services, tool, file, back));

    public void GoHome()
    {
        SelectedNav = NavItems[0];
        CurrentPage = Home;
    }

    public void ShowQueue() => SelectedNav = NavItems[1];

    public void ShowPreferences() => SelectedNav = NavItems[3];

    public void ShowNotification(string text, bool isError = false, string? folder = null, ToolModule? module = null)
    {
        (NotificationTile, NotificationIcon) = Badge(isError, module);
        Notification = text;
        NotificationIsError = isError;
        _notificationFolder = folder;
        OnPropertyChanged(nameof(CanOpenNotificationFolder));
        _notificationTimer.Stop();
        _notificationTimer.Start();
    }

    public bool CanOpenNotificationFolder => _notificationFolder is not null;

    /// <summary>In the window when the user is looking at it, as a desktop notification otherwise.</summary>
    public void Notify(string text, bool isError = false, string? folder = null, ToolModule? module = null)
    {
        if (IsWindowActive || BackgroundNotifier is null)
        {
            ShowNotification(text, isError, folder, module);
            return;
        }
        var (tile, icon) = Badge(isError, module);
        BackgroundNotifier.Show("UniFiles", text, tile, icon, isError, folder);
    }

    private static (IBrush? Tile, Geometry? Icon) Badge(bool isError, ToolModule? module) => module is { } m
        ? (ModuleInfo.For(m).Brush, ModuleInfo.For(m).Icon)
        : (ModuleInfo.Resource<IBrush>("LemonBrush"), ModuleInfo.Resource<Geometry>(isError ? "IconAlert" : "IconCheck"));

    /// <summary>Queues a right-click quick action. Requests arriving close together become one job.</summary>
    public void QueueQuickAction(string toolId, IReadOnlyList<string> files)
    {
        if (!_quickActionFiles.TryGetValue(toolId, out var list))
            _quickActionFiles[toolId] = list = [];
        list.AddRange(files);
        _quickActionTimer.Stop();
        _quickActionTimer.Start();
    }

    private void RunQuickActions()
    {
        _quickActionTimer.Stop();
        var batches = _quickActionFiles.ToList();
        _quickActionFiles.Clear();

        foreach (var (toolId, paths) in batches)
        {
            // A tool with its last settings, or a preset (tool and saved settings).
            var resolved = toolId.StartsWith(LaunchRequest.PresetPrefix, StringComparison.Ordinal)
                ? _services.ResolvePreset(toolId[LaunchRequest.PresetPrefix.Length..])
                : _services.Catalog.Find(toolId) is { } found ? (found, _services.LoadToolSettings(found)) : null;
            if (resolved is not var (tool, settings))
                continue;

            var files = FileKindDetector.ExpandPaths(paths)
                .Where(tool.Accepts)
                // Explorer starts the processes in no particular order: sort by name (matters for merges).
                .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (AppServices.MissingProgramWarning(tool) is { } warning)
            {
                Notify(warning, isError: true, module: tool.Module);
                continue;
            }
            if (files.Count < tool.MinimumInputs)
            {
                Notify(string.Format(Strings.Notification_NotEnoughFiles, tool.Name, tool.MinimumInputs), isError: true, module: tool.Module);
                continue;
            }

            var job = _services.Queue.Enqueue(tool, files, settings, _services.OutputFolder);

            // Quick jobs end with a notification; long ones also say they started.
            DispatcherTimer.RunOnce(() =>
            {
                if (!job.IsFinished)
                    Notify(string.Format(Strings.Notification_Started, tool.Name, Format.Files(files.Count)), module: tool.Module);
            }, TimeSpan.FromSeconds(1.5));
        }
    }

    [RelayCommand]
    private void DismissNotification()
    {
        _notificationTimer.Stop();
        Notification = null;
    }

    [RelayCommand]
    private async Task OpenNotificationFolder()
    {
        if (_notificationFolder is not null)
            await DialogService.OpenFolderAsync(_notificationFolder);
        DismissNotification();
    }

    private void UpdateQueueBadge() =>
        NavItems[1].Badge = _services.Queue.Jobs.Count(j => !j.IsFinished);

    private void OnJobFinished(Job job)
    {
        UpdateQueueBadge();
        if (UpdateWhenIdle && !HasActiveJobs)
        {
            RestartForUpdate?.Invoke();
            return;
        }
        Home.Refresh();
        if (CurrentPage == History)
            History.Refresh();

        if (job.Status == JobStatus.Canceled)
            return;

        var text = job.Status switch
        {
            JobStatus.Completed => string.Format(Strings.Notification_Done, job.Tool.Name, Format.Files(job.DoneCount)),
            JobStatus.CompletedWithErrors => string.Format(Strings.Notification_DoneWithErrors, job.Tool.Name, job.DoneCount, job.FailedCount),
            _ => string.Format(Strings.Notification_Failed, job.Tool.Name),
        };
        if (job.Status == JobStatus.Completed && job.Tool.ShowsSizeChange && Format.Gain(job.InputBytes, job.OutputBytes) is { } gain && job.InputBytes != job.OutputBytes)
            text += " · " + gain;
        if (Format.KeptOriginals(job.KeptOriginalCount) is { } kept)
            text += " · " + kept;

        Notify(text, isError: job.Status != JobStatus.Completed, folder: job.DoneCount > 0 ? job.Output.Path : null, module: job.Tool.Module);
    }
}
