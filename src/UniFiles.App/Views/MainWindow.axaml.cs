using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using UniFiles.App.Controls;
using UniFiles.App.Services;
using UniFiles.Core.Resources;
using UniFiles.App.ViewModels;

namespace UniFiles.App.Views;

public partial class MainWindow : Window
{
    // DragLeave also fires when moving between child controls, so instead the overlay hides
    // once DragOver stops arriving (pointer left the window, drop canceled with Esc...).
    private readonly DispatcherTimer _dragEndTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public MainWindow()
    {
        InitializeComponent();
        AppIcon.Apply(this);
        DialogService.Owner = this;
        DataContextChanged += (_, _) =>
        {
            if (ViewModel is { } vm)
                vm.ReadClipboard = () => ClipboardImport.ReadAsync(this);
        };
        _dragEndTimer.Tick += (_, _) => EndDrag();

        // Notifications go to the desktop when the user isn't looking at the window.
        Activated += (_, _) => SetActive(true);
        Deactivated += (_, _) => SetActive(false);

        // Our own title bar: the tab and the top strip move the window, double-click maximizes.
        TabArea.PointerPressed += OnTitlePressed;
        TitleBar.PointerPressed += OnTitlePressed;
        MinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaximizeButton.Click += (_, _) => ToggleMaximized();
        CloseButton.Click += (_, _) => Close();
        foreach (var grip in Grips.Children.OfType<Border>())
            grip.PointerPressed += OnGripPressed;
        Body.SizeChanged += (_, _) => ClipBody();

        // Document window: Esc or a click outside closes it; it opens at its top.
        DocumentScrim.PointerPressed += (_, _) => ViewModel?.CloseDocumentCommand.Execute(null);
        DocumentLayer.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && DocumentLayer.IsVisible)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    // The text takes the focus: arrows and Page Down scroll it, Tab reaches "Close".
                    DocumentScroller.Offset = default;
                    DocumentScroller.Focus();
                }, DispatcherPriority.Background);
            }
        };

        // Paste to process (Cmd/Ctrl+V), except in a text field where it pastes text as usual.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && ViewModel is { HasDocument: true } open)
            {
                e.Handled = true;
                open.CloseDocumentCommand.Execute(null);
                return;
            }
            var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            if (e.Key != Key.V || e.KeyModifiers != command || FocusManager?.GetFocusedElement() is TextBox || ViewModel is not { } vm)
                return;
            e.Handled = true;
            vm.PasteCommand.Execute(null);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // macOS: window buttons in the tab, where the range chip sits elsewhere; and the usual
        // window shortcuts, which the system doesn't provide without its own title bar.
        if (OperatingSystem.IsMacOS())
        {
            RangeChip.IsVisible = false;
            WindowsCaption.IsVisible = false;
            MacCaption.IsVisible = true;
            MacCloseButton.Click += (_, _) => Close();
            MacMinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;
            MacZoomButton.Click += (_, _) => ToggleMaximized();
            KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.W, KeyModifiers.Meta), Command = new RelayCommand(Close) });
            KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.M, KeyModifiers.Meta), Command = new RelayCommand(() => WindowState = WindowState.Minimized) });
        }

        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void OnTitlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Source is Button)
            return;
        if (e.ClickCount == 2)
            ToggleMaximized();
        else
            BeginMoveDrag(e);
    }

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { Tag: string edge } && WindowState == WindowState.Normal
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginResizeDrag(Enum.Parse<WindowEdge>(edge), e);
        }
    }

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty)
            return;

        // Maximized: no shadow margin, no resize grips, "restore" icon.
        var maximized = WindowState == WindowState.Maximized;
        Frame.Margin = new Thickness(maximized ? 0 : 16);
        Grips.IsVisible = !maximized;
        MaximizeIcon.Data = this.FindResource(maximized ? "IconRestore" : "IconMaximize") as Geometry;
        var label = maximized ? Strings.Window_Restore : Strings.Window_Maximize;
        AutomationProperties.SetName(MaximizeButton, label);
        ToolTip.SetTip(MaximizeButton, label);
        AutomationProperties.SetName(MacZoomButton, label);
        ToolTip.SetTip(MacZoomButton, label);
    }

    /// <summary>Keeps scrolling content inside the folder's rounded outline.</summary>
    private void ClipBody() =>
        Body.Clip = FolderShape.Outline(Body.Bounds.Size, Shape.TabWidth, Shape.TabHeight, Shape.CornerRadius);

    private void SetActive(bool active)
    {
        if (ViewModel is { } vm)
            vm.IsWindowActive = active;
    }

    private static bool HasFiles(DragEventArgs e) => e.DataTransfer.Contains(DataFormat.File);

    private void OnDragEnter(object? sender, DragEventArgs e) => OnDragOver(sender, e);

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasFiles = HasFiles(e);
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        if (!hasFiles || ViewModel is not { } vm)
            return;
        vm.IsDragging = true;
        _dragEndTimer.Stop();
        _dragEndTimer.Start();
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        _dragEndTimer.Stop();
        _dragEndTimer.Start();
    }

    private void EndDrag()
    {
        _dragEndTimer.Stop();
        if (ViewModel is { } vm)
            vm.IsDragging = false;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        EndDrag();
        if (ViewModel is not { } vm)
            return;

        var paths = e.DataTransfer.TryGetFiles()?.Select(item => item.TryGetLocalPath()).OfType<string>().ToList();
        if (paths is { Count: > 0 })
            vm.OpenFiles(paths);
    }
}
