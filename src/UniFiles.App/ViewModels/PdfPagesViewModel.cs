using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniFiles.App.Services;
using UniFiles.Core.Resources;
using UniFiles.Core.Tools;
using UniFiles.Core.Tools.Pdf;

namespace UniFiles.App.ViewModels;

/// <summary>A page in the left column of a PDF editor.</summary>
public sealed partial class FormPageViewModel(int index) : ObservableObject
{
    public int Index { get; } = index;

    public string Label => string.Format(Strings.Organizer_Page, Index + 1);

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private bool _isCurrent;
}

/// <summary>
/// What the PDF editors share (form, signature): page thumbnails, the current page drawn by PDFium,
/// zoom, page navigation, and conversion between PDF points (unrotated, origin bottom-left) and
/// display units (rotated, origin top-left).
/// </summary>
public abstract partial class PdfPagesViewModel : PageViewModel, IDisposable
{
    /// <summary>Display units (DIP) per PDF point at 100 % zoom.</summary>
    public const double Scale = 1.5;

    private const int ThumbnailSize = 150;

    protected readonly MainWindowViewModel Main;
    protected readonly AppServices Services;
    private readonly PageViewModel _back;
    private readonly CancellationTokenSource _cancellation = new();
    private PdfRenderer? _renderer;
    private IReadOnlyList<PdfPageGeometry> _geometry = [];
    private string? _displayFile;

    protected PdfPagesViewModel(MainWindowViewModel main, AppServices services, string file, PageViewModel back)
    {
        Main = main;
        Services = services;
        _back = back;
        FilePath = file;
        FileName = Path.GetFileName(file);
    }

    public string FilePath { get; }

    public string FileName { get; }

    public ObservableCollection<FormPageViewModel> Pages { get; } = [];

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => Error is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageLabel), nameof(PageWidth), nameof(PageHeight))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    private int _currentPage;

    [ObservableProperty]
    private Bitmap? _pageImage;

    [ObservableProperty]
    private double _zoom = 1;

    /// <summary>Raised when the overlays of the current page must be laid out again.</summary>
    public event EventHandler? LayoutChanged;

    public string PageLabel => string.Format(Strings.Form_PageOf, CurrentPage + 1, Math.Max(1, Pages.Count));

    public double PageWidth => DisplaySize(CurrentPage).Width;

    public double PageHeight => DisplaySize(CurrentPage).Height;

    /// <summary>Loading or error state changed (commands that depend on them).</summary>
    protected virtual void OnStateChanged()
    {
    }

    partial void OnIsLoadingChanged(bool value) => OnStateChanged();

    partial void OnErrorChanged(string? value) => OnStateChanged();

    protected void RaiseLayoutChanged() => LayoutChanged?.Invoke(this, EventArgs.Empty);

    // ---- Loading -------------------------------------------------------------------------------

    /// <summary>
    /// Opens the pages for display (after the subclass has read what it needs). <paramref name="displayFile"/>:
    /// another file to draw, such as a copy with the form's values drawn in; deleted when the editor closes.
    /// </summary>
    protected async Task OpenPagesAsync(IReadOnlyList<PdfPageGeometry> geometry, string? displayFile = null)
    {
        _geometry = geometry;
        _displayFile = displayFile;
        _renderer = await Task.Run(() => PdfRenderer.Open(displayFile ?? FilePath));
        for (var i = 0; i < _renderer.PageCount; i++)
            Pages.Add(new FormPageViewModel(i));
    }

    /// <summary>Shows a page (the first by default) and draws the thumbnails, once loading succeeded.</summary>
    protected async Task ShowPagesAsync(int first = 0)
    {
        if (Error is not null || Pages.Count == 0)
            return;
        await ShowPageAsync(Math.Clamp(first, 0, Pages.Count - 1));
        await RenderThumbnailsAsync();
    }

    private async Task RenderThumbnailsAsync()
    {
        foreach (var page in Pages.ToList())
        {
            if (_cancellation.IsCancellationRequested || _renderer is not { } renderer)
                return;
            try
            {
                var (pixels, width, height) = await Task.Run(() => (renderer.Render(page.Index, ThumbnailSize, out var w, out var h), w, h));
                page.Thumbnail = ToBitmap(pixels, width, height);
            }
            catch (ToolException)
            {
                // A page PDFium can't draw keeps an empty thumbnail.
            }
        }
    }

    protected async Task ShowPageAsync(int index)
    {
        if (_renderer is not { } renderer || index < 0 || index >= Pages.Count)
            return;
        CurrentPage = index;
        foreach (var page in Pages)
            page.IsCurrent = page.Index == index;
        // Drawn at twice the display size, for sharp text on high-density screens.
        var (pixels, width, height) = await Task.Run(() => (renderer.RenderAtDpi(index, (int)(72 * Scale * 2), out var w, out var h), w, h));
        var old = PageImage;
        PageImage = ToBitmap(pixels, width, height);
        old?.Dispose();
        RaiseLayoutChanged();
    }

    private static unsafe WriteableBitmap ToBitmap(byte[] pixels, int width, int height)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var target = bitmap.Lock();
        fixed (byte* source = pixels)
        {
            for (var y = 0; y < height; y++)
                Buffer.MemoryCopy(source + (long)y * width * 4, (byte*)target.Address + (long)y * target.RowBytes, target.RowBytes, width * 4);
        }
        return bitmap;
    }

    // ---- Coordinates: PDF points (unrotated, origin bottom-left) ↔ display (rotated, top-left) ----

    public Size DisplaySize(int page)
    {
        if (page < 0 || page >= _geometry.Count)
            return new Size(612 * Scale, 792 * Scale);
        var g = _geometry[page];
        var (w, h) = g.Rotate is 90 or 270 ? (g.MediaBox.Height, g.MediaBox.Width) : (g.MediaBox.Width, g.MediaBox.Height);
        return new Size(w * Scale, h * Scale);
    }

    public Rect ToDisplay(int page, PdfRect rect)
    {
        var a = ToDisplay(page, rect.Left, rect.Bottom);
        var b = ToDisplay(page, rect.Right, rect.Top);
        return new Rect(new Point(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new Point(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
    }

    private Point ToDisplay(int page, double x, double y)
    {
        var g = page >= 0 && page < _geometry.Count ? _geometry[page] : new PdfPageGeometry(new PdfRect(0, 0, 612, 792), 0);
        var (w, h) = (g.MediaBox.Width, g.MediaBox.Height);
        var (px, py) = (x - g.MediaBox.Left, y - g.MediaBox.Bottom);
        var (dx, dy) = g.Rotate switch
        {
            90 => (py, px),
            180 => (w - px, py),
            270 => (h - py, w - px),
            _ => (px, h - py),
        };
        return new Point(dx * Scale, dy * Scale);
    }

    public PdfRect FromDisplay(int page, Rect rect)
    {
        var a = FromDisplay(page, rect.TopLeft);
        var b = FromDisplay(page, rect.BottomRight);
        return new PdfRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }

    private Point FromDisplay(int page, Point point)
    {
        var g = page >= 0 && page < _geometry.Count ? _geometry[page] : new PdfPageGeometry(new PdfRect(0, 0, 612, 792), 0);
        var (w, h) = (g.MediaBox.Width, g.MediaBox.Height);
        var (dx, dy) = (point.X / Scale, point.Y / Scale);
        var (px, py) = g.Rotate switch
        {
            90 => (dy, dx),
            180 => (w - dx, dy),
            270 => (w - dy, h - dx),
            _ => (dx, h - dy),
        };
        return new Point(px + g.MediaBox.Left, py + g.MediaBox.Bottom);
    }

    // ---- Navigation ----------------------------------------------------------------------------

    private bool CanGoBack() => CurrentPage > 0;

    private bool CanGoForward() => CurrentPage < Pages.Count - 1;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task PreviousPage() => ShowPageAsync(CurrentPage - 1);

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private Task NextPage() => ShowPageAsync(CurrentPage + 1);

    [RelayCommand]
    private Task SelectPage(FormPageViewModel page) => ShowPageAsync(page.Index);

    [RelayCommand]
    private void ZoomIn() => Zoom = Math.Min(2, Math.Round(Zoom + 0.25, 2));

    [RelayCommand]
    private void ZoomOut() => Zoom = Math.Max(0.3, Math.Round(Zoom - 0.25, 2));

    [RelayCommand]
    private void Back()
    {
        Dispose();
        Main.ShowPage(_back);
    }

    /// <summary>Leaves the editor for the queue, where the job just added runs.</summary>
    protected void Finish()
    {
        Dispose();
        Main.ShowQueue();
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _renderer?.Dispose();
        _renderer = null;
        if (_displayFile is { } copy)
        {
            try
            {
                File.Delete(copy);
            }
            catch (IOException)
            {
                // Left in the temp folder, which the system cleans.
            }
            _displayFile = null;
        }
        GC.SuppressFinalize(this);
    }
}

/// <summary>A page that refreshes itself when shown again (back from a page it opened).</summary>
public interface IShownAgain
{
    void OnShownAgain();
}
