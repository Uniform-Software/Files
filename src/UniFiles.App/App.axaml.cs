using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using UniFiles.App.Platform;
using UniFiles.App.Services;
using UniFiles.App.ViewModels;
using UniFiles.App.Views;
using UniFiles.Core.Jobs;
using UniFiles.Core.Resources;
using UniFiles.Core.Storage;
using UniFiles.Core.Tools;

namespace UniFiles.App;

/// <summary>
/// Application lifetime: UniFiles keeps running in the notification area so jobs continue and
/// right-click actions start instantly. It quits from the tray menu (or on close, if the user
/// turned that preference off). On macOS the Dock icon also brings the window back, and files
/// opened from the Finder (Open With, drop on the Dock icon) arrive as activation events, not arguments.
/// </summary>
public partial class App : Application
{
    private const int MaxJobsInTrayMenu = 5;
    private static readonly TimeSpan MinimumSplashTime = TimeSpan.FromSeconds(1.8);

    private AppServices _services = null!;
    private MainWindowViewModel _viewModel = null!;
    private IClassicDesktopStyleApplicationLifetime _desktop = null!;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _trayDirty;
    private bool _quitting;

    // Requests received from the system before the main window is ready (macOS file activation).
    private List<LaunchRequest>? _early = [];

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            CrashLog.InstallForDispatcher();
            var initial = Program.InitialRequest;

            // Cmd+Q, logout: quit for real, the window must not hide itself instead.
            desktop.ShutdownRequested += (_, _) => PrepareQuit();
            if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
                activatable.Activated += OnActivated;
            if (OperatingSystem.IsMacOS())
                SetUpMacMenu();

            // The splash greets interactive launches; right-click actions stay in the background.
            SplashWindow? splash = null;
            if (initial.Mode == LaunchMode.Open)
            {
                splash = new SplashWindow(Motion.SystemPrefersReducedMotion());
                splash.Show();
            }
            Dispatcher.UIThread.Post(() => _ = StartAsync(initial, splash));
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Loads everything while the splash shows the steps, then opens the main window.</summary>
    private async Task StartAsync(LaunchRequest initial, SplashWindow? splash)
    {
        var clock = Stopwatch.StartNew();

        splash?.SetStatus(Strings.Splash_Modules);
        _services = await Task.Run(() => AppServices.Current);

        splash?.SetStatus(Strings.Splash_Programs);
        await Task.Run(() =>
        {
            ExternalTools.FindFfmpeg();
            ExternalTools.FindGhostscript();
        });

        splash?.SetStatus(Strings.Splash_Interface);
        ApplyTheme(_services.Settings.Theme);
        _viewModel = new MainWindowViewModel(_services) { BackgroundNotifier = new ToastNotifier() };
        SetUpTray();
        RefreshContextMenu();

        // Long enough to be seen and read, never longer than needed beyond that.
        if (splash is not null && MinimumSplashTime - clock.Elapsed is { } remaining && remaining > TimeSpan.Zero)
            await Task.Delay(remaining);

        // A right-click quick action works in the background: no window unless asked for.
        if (initial.Mode != LaunchMode.QuickAction)
            ShowMainWindow();
        Handle(initial);
        splash?.Close();

        var early = _early!;
        _early = null;
        early.ForEach(Handle);

        SingleInstance.Attach(request => Dispatcher.UIThread.Post(() => Handle(request)));

        // Once the window is up and idle, get the video player ready.
        DispatcherTimer.RunOnce(VideoPlayer.WarmUp, TimeSpan.FromSeconds(3));
    }

    public static void ApplyTheme(AppTheme theme)
    {
        if (Current is not null)
            Current.RequestedThemeVariant = theme == AppTheme.Light ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    private void Handle(LaunchRequest request)
    {
        switch (request.Mode)
        {
            case LaunchMode.QuickAction when request.ToolId is { } toolId:
                _viewModel.QueueQuickAction(toolId, request.Files);
                break;
            case LaunchMode.Open:
                ShowMainWindow();
                if (request.Files.Count > 0)
                    _viewModel.OpenFiles(request.Files);
                break;
        }
    }

    /// <summary>macOS: files opened from the Finder, or a click on the Dock icon while the window is hidden.</summary>
    private void OnActivated(object? sender, ActivatedEventArgs e)
    {
        LaunchRequest? request = e switch
        {
            FileActivatedEventArgs files => new(LaunchMode.Open, files.Files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList()),
            { Kind: ActivationKind.Reopen } => new(LaunchMode.Open, []),
            _ => null,
        };
        if (request is null)
            return;
        if (_early is not null)
            _early.Add(request);
        else
            Handle(request);
    }

    private void ShowMainWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow { DataContext = _viewModel };
            _window.Closing += OnWindowClosing;
            _desktop.MainWindow = _window;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_quitting)
            return;
        if (_services.Settings.KeepInTray && _tray is not null)
        {
            // Hide instead of closing: running jobs continue, the tray icon brings it back.
            e.Cancel = true;
            _window?.Hide();
        }
        else
        {
            Quit();
        }
    }

    private void Quit()
    {
        PrepareQuit();
        _desktop.Shutdown();
    }

    private void PrepareQuit()
    {
        _quitting = true;
        if (_services is not null)
        {
            foreach (var job in _services.Queue.Jobs.Where(j => !j.IsFinished))
                _services.Queue.Cancel(job);
        }
        _tray?.Dispose();
        _tray = null;
    }

    /// <summary>
    /// macOS application menu: "Preferences…" (Cmd+,) above the standard items (Hide, Quit…),
    /// in place of Avalonia's default "About Avalonia".
    /// </summary>
    private void SetUpMacMenu()
    {
        var preferences = new NativeMenuItem(Strings.Menu_Preferences) { Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Meta) };
        preferences.Click += (_, _) =>
        {
            if (_early is not null)
                return;
            ShowMainWindow();
            _viewModel.ShowPreferences();
        };
        NativeMenu.SetMenu(this, new NativeMenu { Items = { preferences } });
    }

    /// <summary>Re-applies the right-click menu if enabled, so labels and the executable path stay current.</summary>
    private void RefreshContextMenu()
    {
        if (!_services.Settings.ContextMenuEnabled || !ExplorerMenu.Installer.IsSupported || Environment.ProcessPath is not { } exe)
            return;
        try
        {
            ExplorerMenu.Installer.Install(exe, _services.Catalog);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // Keep the previous menu; the preference screen reports errors when toggled.
        }
    }

    // ---- Notification area -------------------------------------------------------------

    private void SetUpTray()
    {
        // macOS menu bar: the mark alone as a template image, which the system tints like its own
        // icons (white on a dark menu bar, black on a light one).
        var icon = OperatingSystem.IsMacOS() ? "unifiles-tray-mac.png" : "unifiles-icon.png";
        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri($"avares://UniFiles/Assets/{icon}"))),
            ToolTipText = Strings.Tray_Tooltip,
            Menu = new NativeMenu(),
            IsVisible = true,
        };
        if (OperatingSystem.IsMacOS())
            MacOSProperties.SetIsTemplateIcon(_tray, true);
        _tray.Clicked += (_, _) => ShowMainWindow();
        TrayIcon.SetIcons(this, [_tray]);

        // Rebuilt at most twice a second while jobs progress.
        _services.Queue.JobsChanged += (_, _) => _trayDirty = true;
        _services.Queue.JobFinished += (_, _) => _trayDirty = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            if (_trayDirty || _services.Queue.Jobs.Any(j => j.Status == JobStatus.Running))
                RefreshTray();
        };
        timer.Start();
        RefreshTray();
    }

    private void RefreshTray()
    {
        if (_tray?.Menu is not { } menu)
            return;
        _trayDirty = false;

        var active = _services.Queue.Jobs.Where(j => !j.IsFinished).ToList();
        _tray.ToolTipText = active.Count == 0 ? Strings.Tray_Tooltip : string.Format(Strings.Tray_TooltipActive, Format.Files(active.Sum(j => j.Items.Count)));

        menu.Items.Clear();
        menu.Items.Add(Item(Strings.Tray_Open, ShowMainWindow));
        menu.Items.Add(new NativeMenuItemSeparator());

        if (active.Count == 0)
        {
            menu.Items.Add(new NativeMenuItem(Strings.Tray_NoJobs) { IsEnabled = false });
        }
        else
        {
            foreach (var job in active.Take(MaxJobsInTrayMenu))
            {
                var processed = job.Items.Count(i => i.Status is not (WorkStatus.Pending or WorkStatus.Running));
                var label = string.Format(Strings.Tray_Job, job.Tool.Name, processed, job.Items.Count);
                if (job.Status == JobStatus.Paused)
                    label += " · " + Strings.Status_Paused;
                menu.Items.Add(Item(label, () =>
                {
                    ShowMainWindow();
                    _viewModel.ShowQueue();
                }));
            }

            if (active.Any(j => j.Status is JobStatus.Running or JobStatus.Pending))
                menu.Items.Add(Item(Strings.Tray_PauseAll, () => active.ForEach(j => j.Pause())));
            if (active.Any(j => j.Status == JobStatus.Paused))
                menu.Items.Add(Item(Strings.Tray_ResumeAll, () => active.ForEach(j => j.Resume())));
        }

        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item(Strings.Tray_OpenOutput, () => _ = DialogService.OpenFolderAsync(_services.Settings.OutputDirectory, _window)));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item(Strings.Tray_Quit, Quit));
    }

    private static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }
}
