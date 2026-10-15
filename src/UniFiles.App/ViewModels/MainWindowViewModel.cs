using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniFiles.App.Platform;
using UniFiles.App.Services;
using UniFiles.Core;
using UniFiles.Core.Jobs;
using UniFiles.Core.Resources;
using UniFiles.Core.Storage;
using UniFiles.Core.Tools.Pdf;
using UniFiles.Core.Tools.Video;

namespace UniFiles.App.ViewModels;

/// <summary>Base class of the screens shown in the main area.</summary>
public abstract class PageViewModel : ObservableObject;

/// <summary>A text shown in a window over the page (notices, release notes).</summary>
public sealed record DocumentViewModel(string Title, string Markdown);

/// <summary>
/// An entry of the sidebar. Sub-entries (<see cref="IsSub"/>) sit under their parent (Preferences)
/// and show only while one of them is open.
/// </summary>
public sealed class NavItemViewModel(string title, string iconKey, PageViewModel page, bool isSub = false, PreferencesSection? section = null) : ObservableObject
{
    private int _badge;
    private bool _isShown = !isSub;
    private bool _isOpen;

    public string Title { get; } = title;

    public Geometry? Icon { get; } = ModuleInfo.Resource<Geometry>(iconKey);

    public PageViewModel Page { get; } = page;

    public bool IsSub { get; } = isSub;

    /// <summary>The part of the Preferences page this sub-entry shows.</summary>
    public PreferencesSection? Section { get; } = section;

    /// <summary>Visible in the sidebar (sub-entries: while their group is open).</summary>
    public bool IsShown
    {
        get => _isShown;
        set => SetProperty(ref _isShown, value);
    }

    /// <summary>A parent whose sub-entries are showing.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        set => SetProperty(ref _isOpen, value);
    }

    public bool HasChildren { get; init; }

    public Thickness ItemPadding => IsSub ? new Thickness(44, 6, 12, 6) : new Thickness(12, 10);

    public double ItemHeight => IsSub ? 34 : 44;

    public double ItemFontSize => IsSub ? 13 : 14;

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
        History = new HistoryViewModel(this, services);
        Preferences = new PreferencesViewModel(this, services);
        Automations = new AutomationsViewModel(services);
        Signatures = new SignatureSettingsViewModel(this, services);

        NavItems =
        [
            new NavItemViewModel(Strings.Nav_Home, "IconHome", Home),
            new NavItemViewModel(Strings.Nav_Queue, "IconQueue", Queue),
            new NavItemViewModel(Strings.Nav_History, "IconHistory", History),
            new NavItemViewModel(Strings.Nav_Automations, "IconAutomation", Automations),
            new NavItemViewModel(Strings.Nav_Preferences, "IconSettings", Preferences) { HasChildren = true },
            new NavItemViewModel(Strings.Prefs_General, "", Preferences, isSub: true, PreferencesSection.General),
            new NavItemViewModel(Strings.Prefs_Appearance, "", Preferences, isSub: true, PreferencesSection.Appearance),
            new NavItemViewModel(Strings.Prefs_System, "", Preferences, isSub: true, PreferencesSection.System),
            new NavItemViewModel(Strings.Update_Title, "", Preferences, isSub: true, PreferencesSection.Updates),
            new NavItemViewModel(Strings.Prefs_Signatures, "", Signatures, isSub: true),
            new NavItemViewModel(Strings.Prefs_AboutTitle, "", Preferences, isSub: true, PreferencesSection.About),
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

    public AutomationsViewModel Automations { get; }

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

    /// <summary>Set by the window: what the clipboard holds, as local files.</summary>
    public Func<Task<IReadOnlyList<string>>>? ReadClipboard { get; set; }

    /// <summary>Cmd/Ctrl+V outside a text field: process the copied files or picture.</summary>
    [RelayCommand]
    public async Task Paste()
    {
        var files = ReadClipboard is null ? [] : await ReadClipboard();
        if (files.Count == 0)
        {
            ShowNotification(Strings.Paste_Nothing, isError: true);
            return;
        }
        OpenFiles(files);
    }

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
        // "Preferences" opens its group on its first part.
        if (value.HasChildren)
        {
            var first = NavItems[NavItems.IndexOf(value) + 1];
            Dispatcher.UIThread.Post(() => SelectedNav = first);
            return;
        }
        var parent = NavItems.First(n => n.HasChildren);
        var inGroup = value.IsSub;
        parent.IsOpen = inGroup;
        foreach (var item in NavItems.Where(n => n.IsSub))
            item.IsShown = inGroup;
        if (value.Section is { } section)
            Preferences.Section = section;
        if (value.Page == Signatures)
            Signatures.Refresh();
        CurrentPage = value.Page;
        if (value.Page == History)
            History.Refresh();
        if (value.Page == Home)
            Home.Refresh();
    }

    /// <summary>Entry point for dropped, picked or command-line files.</summary>
    /// <param name="recipe">Settings to start from (history's "Do again") and when they were used.</param>
    public void OpenFiles(IEnumerable<string> paths, ToolModule? module = null, (ToolPreset Recipe, DateTime When)? recipe = null)
    {
        var files = FileKindDetector.ExpandInputs(paths);
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

        if (recipe is { } r)
            action.ApplyRecipe(r.Recipe, r.When);
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
        (page as IShownAgain)?.OnShownAgain();
    }

    public void OpenSignEditor(PdfSignTool tool, string file, PageViewModel back) =>
        ShowPage(new PdfSignEditorViewModel(this, _services, tool, file, back));

    /// <summary>"Check signatures": the report in a window over the page.</summary>
    public async Task ShowSignatureReportAsync(IReadOnlyList<string> files)
    {
        var report = await Task.Run(() => PdfVerifyTool.Report(files));
        Document = new DocumentViewModel(Strings.Tool_PdfVerify_Name, report);
    }

    public void OpenTrimEditor(VideoTrimTool tool, string file, PageViewModel back) =>
        ShowPage(new TrimEditorViewModel(this, _services, tool, file, back));

    public void OpenPdfOrganizer(PdfOrganizeTool tool, string file, PageViewModel back) =>
        ShowPage(new PdfOrganizerViewModel(this, _services, tool, file, back));

    public void OpenFormEditor(PdfFormFillTool tool, string file, PageViewModel back) =>
        ShowPage(new PdfFormEditorViewModel(this, _services, tool, file, back));

    public SignatureSettingsViewModel Signatures { get; }

    /// <summary>The saved signatures (Preferences › Signatures); <paramref name="returnTo"/>: the signing screen to go back to.</summary>
    public void OpenSignatureSettings(PageViewModel? returnTo = null)
    {
        Signatures.ReturnTo = returnTo;
        SelectedNav = NavItems.First(n => n.Page == Signatures);
    }

    /// <summary>A page shown inside a Preferences part (signature creation, a signature's details): the sidebar stays put.</summary>
    public void ShowInPreferences(PageViewModel page) => CurrentPage = page;

    public void GoHome()
    {
        SelectedNav = NavItems[0];
        CurrentPage = Home;
    }

    public void ShowQueue() => SelectedNav = NavItems.First(n => n.Page == Queue);

    public void ShowPreferences() => SelectedNav = NavItems.First(n => n.Section == PreferencesSection.General);

    // ---- Documents (window over the page) ---------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDocument))]
    private DocumentViewModel? _document;

    public bool HasDocument => Document is not null;

    [RelayCommand]
    private void ShowReleaseNotes() => Document = new DocumentViewModel(Strings.Doc_ReleaseNotes, Documents.AllReleaseNotes());

    [RelayCommand]
    private void ShowLicenses() => Document = new DocumentViewModel(Strings.Prefs_ThirdParty, Documents.ThirdPartyNotices());

    [RelayCommand]
    private void CloseDocument() => Document = null;

    private bool _whatsNewShown;

    /// <summary>After an update, the new version's notes, once, when the window first shows.</summary>
    public void ShowWhatsNewIfUpdated()
    {
        if (_whatsNewShown || !_services.JustUpdated)
            return;
        _whatsNewShown = true;
        if (Documents.ReleaseNotesFor(AppServices.CurrentVersion) is { } notes)
            Document = new DocumentViewModel(string.Format(Strings.Doc_WhatsNew, AppServices.CurrentVersion.ToString(3)), notes);
    }

    public void ShowNotification(string text, bool isError = false, string? folder = null, ToolModule? module = null)
    {
        (NotificationTile, NotificationIcon) = Badge(isError, module);
        Notification = text;
        NotificationIsError = isError;
        _notificationFolder = folder;
        OnPropertyChanged(nameof(NotificationActionLabel));
        OnPropertyChanged(nameof(CanOpenNotificationFolder));
        _notificationTimer.Stop();
        _notificationTimer.Start();
    }

    public bool CanOpenNotificationFolder => _notificationFolder is not null;

    /// <summary>"Show in Finder" for a result file, "Open folder" for a folder.</summary>
    public string NotificationActionLabel => PlatformText.ShowLabel(_notificationFolder);

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

            var files = FileKindDetector.ExpandInputs(paths)
                .Where(f => tool.Accepts(f.Path))
                // Explorer starts the processes in no particular order: sort by name (matters for merges).
                .OrderBy(f => Path.GetFileName(f.Path), StringComparer.CurrentCultureIgnoreCase)
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

            var job = _services.Queue.Enqueue(tool, files, settings, _services.TargetFor(settings));

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
            await DialogService.ShowAsync(_notificationFolder);
        DismissNotification();
    }

    private void UpdateQueueBadge() =>
        NavItems.First(n => n.Page == Queue).Badge = _services.Queue.Jobs.Count(j => !j.IsFinished);

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

        // The button shows the first result selected in its folder.
        Notify(text, isError: job.Status != JobStatus.Completed, folder: job.DoneCount > 0 ? job.FirstOutput ?? job.Output.Path : null, module: job.Tool.Module);
    }
}
