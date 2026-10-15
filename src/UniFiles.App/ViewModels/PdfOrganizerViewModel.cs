using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniFiles.App.Services;
using UniFiles.Core.Resources;
using UniFiles.Core.Tools;
using UniFiles.Core.Tools.Pdf;

namespace UniFiles.App.ViewModels;

/// <summary>One page in the organizer: its original number, added rotation, and whether it's kept.</summary>
public sealed partial class PageTileViewModel(int sourceIndex) : ObservableObject
{
    public int SourceIndex { get; } = sourceIndex;

    /// <summary>Number in the original document, as people know it.</summary>
    public string Label => string.Format(Strings.Organizer_Page, SourceIndex + 1);

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Transform))]
    private int _rotation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemoveLabel))]
    private bool _isRemoved;

    /// <summary>Highlighted while another page is dragged over it.</summary>
    [ObservableProperty]
    private bool _isDropTarget;

    public ITransform Transform => new RotateTransform(Rotation);

    public string RemoveLabel => IsRemoved ? Strings.Organizer_Restore : Strings.Organizer_Remove;

    public override string ToString() => IsRemoved ? $"{Label}, {Strings.Organizer_Removed}" : Label;
}

/// <summary>
/// Page organizer: thumbnails of every page, reordered by drag and drop (or the arrow buttons),
/// rotated and removed one by one, then written as a new PDF.
/// </summary>
public partial class PdfOrganizerViewModel : PageViewModel, IDisposable
{
    private const int ThumbnailSize = 240;

    private readonly MainWindowViewModel _main;
    private readonly AppServices _services;
    private readonly PdfOrganizeTool _tool;
    private readonly PageViewModel _back;
    private readonly CancellationTokenSource _cancellation = new();

    public PdfOrganizerViewModel(MainWindowViewModel main, AppServices services, PdfOrganizeTool tool, string file, PageViewModel back)
    {
        _main = main;
        _services = services;
        _tool = tool;
        _back = back;
        FilePath = file;
        FileName = Path.GetFileName(file);
        Pages.CollectionChanged += (_, _) => Refresh();
        _ = LoadAsync();
    }

    public string FilePath { get; }

    public string FileName { get; }

    public ObservableCollection<PageTileViewModel> Pages { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private bool _isLoading = true;

    [ObservableProperty]
    private string? _error;

    public bool HasError => Error is not null;

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    public string Summary
    {
        get
        {
            var kept = Pages.Count(p => !p.IsRemoved);
            return string.Format(Strings.Organizer_Summary, kept, Pages.Count);
        }
    }

    private async Task LoadAsync()
    {
        PdfRenderer renderer;
        try
        {
            renderer = await Task.Run(() => PdfRenderer.Open(FilePath));
        }
        catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException or DllNotFoundException)
        {
            Error = ex is ToolException ? ex.Message : Strings.Error_PdfUnreadable;
            IsLoading = false;
            return;
        }

        using (renderer)
        {
            for (var i = 0; i < renderer.PageCount; i++)
            {
                var tile = new PageTileViewModel(i);
                tile.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(PageTileViewModel.IsRemoved))
                        Refresh();
                };
                Pages.Add(tile);
            }
            IsLoading = false;

            // Thumbnails arrive one by one, in reading order.
            foreach (var tile in Pages.ToList())
            {
                if (_cancellation.IsCancellationRequested)
                    return;
                try
                {
                    var (pixels, width, height) = await Task.Run(() =>
                    {
                        var data = renderer.Render(tile.SourceIndex, ThumbnailSize, out var w, out var h);
                        return (data, w, h);
                    });
                    tile.Thumbnail = ToBitmap(pixels, width, height);
                }
                catch (ToolException)
                {
                    // A page PDFium can't draw keeps its placeholder; it is still exported.
                }
            }
        }
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

    private void Refresh()
    {
        OnPropertyChanged(nameof(Summary));
        ExportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Moves a page before or after another (drag and drop).</summary>
    public void Move(PageTileViewModel page, PageTileViewModel target)
    {
        var from = Pages.IndexOf(page);
        var to = Pages.IndexOf(target);
        if (from < 0 || to < 0 || from == to)
            return;
        Pages.Move(from, to);
    }

    [RelayCommand]
    private void MoveLeft(PageTileViewModel page)
    {
        var index = Pages.IndexOf(page);
        if (index > 0)
            Pages.Move(index, index - 1);
    }

    [RelayCommand]
    private void MoveRight(PageTileViewModel page)
    {
        var index = Pages.IndexOf(page);
        if (index >= 0 && index < Pages.Count - 1)
            Pages.Move(index, index + 1);
    }

    [RelayCommand]
    private void RotateLeft(PageTileViewModel page) => page.Rotation = (page.Rotation + 270) % 360;

    [RelayCommand]
    private void RotateRight(PageTileViewModel page) => page.Rotation = (page.Rotation + 90) % 360;

    [RelayCommand]
    private void ToggleRemoved(PageTileViewModel page) => page.IsRemoved = !page.IsRemoved;

    [RelayCommand]
    private void RotateAll()
    {
        foreach (var page in Pages)
            page.Rotation = (page.Rotation + 90) % 360;
    }

    [RelayCommand]
    private void Reset()
    {
        var original = Pages.OrderBy(p => p.SourceIndex).ToList();
        Pages.Clear();
        foreach (var page in original)
        {
            page.Rotation = 0;
            page.IsRemoved = false;
            Pages.Add(page);
        }
    }

    private bool CanExport() => !IsLoading && Error is null && Pages.Any(p => !p.IsRemoved);

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void Export()
    {
        var settings = ToolSettings.FromDefaults(_tool);
        settings[PdfOrganizeTool.PagesKey] = PdfOrganizeTool.FormatPages(
            Pages.Where(p => !p.IsRemoved).Select(p => new PageChoice(p.SourceIndex, p.Rotation)));
        _services.Queue.Enqueue(_tool, [InputFile.Of(FilePath)], settings, _services.TargetFor(settings));
        Dispose();
        _main.ShowQueue();
    }

    [RelayCommand]
    private void Back()
    {
        Dispose();
        _main.ShowPage(_back);
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var page in Pages)
            {
                page.Thumbnail?.Dispose();
                page.Thumbnail = null;
            }
        });
    }
}
