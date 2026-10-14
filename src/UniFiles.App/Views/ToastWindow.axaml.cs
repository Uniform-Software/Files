using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using UniFiles.App.Services;

namespace UniFiles.App.Views;

public partial class ToastWindow : Window
{
    private static readonly List<ToastWindow> Open = [];
    private const int ScreenMargin = 16;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(8) };

    public ToastWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
        _timer.Tick += (_, _) => Close();
        // Keep it on screen while the pointer is over it.
        PointerEntered += (_, _) => _timer.Stop();
        PointerExited += (_, _) => _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            Open.Remove(this);
            Restack();
        };
    }

    /// <summary>Shows a notification in the bottom-right corner, stacked above the previous ones.</summary>
    public static void Show(string title, string message, IBrush? accent, Geometry? icon, bool isError, string? folder)
    {
        var toast = new ToastWindow();
        toast.TitleText.Text = title;
        toast.MessageText.Text = message;
        toast.MessageText.Classes.Set("error", isError);
        toast.Badge.Tile = accent;
        toast.Badge.Icon = icon;
        // A file is shown selected in its folder, a folder is opened.
        toast.OpenFolderButton.IsVisible = folder is not null;
        toast.OpenFolderButton.Content = PlatformText.ShowLabel(folder);
        toast.OpenFolderButton.Click += async (_, _) =>
        {
            if (folder is not null)
                await DialogService.ShowAsync(folder, toast);
            toast.Close();
        };

        Open.Add(toast);
        // Measure first so the window appears directly in the corner, then follow its real size.
        if (toast.Content is Control content)
        {
            content.Measure(new Size(toast.Width, double.PositiveInfinity));
            toast._height = content.DesiredSize.Height;
        }
        Restack();
        toast.Resized += (_, e) =>
        {
            toast._height = e.ClientSize.Height;
            Restack();
        };
        toast.Show();
        toast._timer.Start();
    }

    private double _height;

    /// <summary>Stacks notifications from the bottom-right corner of the main screen, newest at the bottom.</summary>
    private static void Restack()
    {
        var bottom = 0;
        foreach (var toast in Open.AsEnumerable().Reverse())
        {
            if (toast.Screens.Primary is not { } screen)
                continue;
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            var width = (int)(toast.Width * scale);
            var height = (int)(toast._height * scale);
            var margin = (int)(ScreenMargin * scale);
            toast.Position = new PixelPoint(area.Right - width - margin, area.Bottom - height - margin - bottom);
            bottom += height + margin / 2;
        }
    }
}
