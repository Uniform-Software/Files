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

public sealed record FieldKindChoice(FormFieldKind Kind, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A field of the form being filled: an existing one, or one added in the editor.</summary>
public sealed partial class FormFieldViewModel : ObservableObject
{
    public FormFieldViewModel(FormField field)
    {
        _name = field.Name;
        _kind = field.Kind;
        Options = field.Options;
        ReadOnly = field.ReadOnly;
        _required = field.Required;
        _fontSize = field.FontSize;
        Widgets = field.Widgets.Count > 0 ? field.Widgets.ToList() : [new FormWidget(field.Page, field.Rect, null)];
        _text = field.Kind is FormFieldKind.Text or FormFieldKind.MultilineText or FormFieldKind.Choice ? field.Value ?? "" : "";
        _isChecked = field.Kind == FormFieldKind.CheckBox && field.Value == PdfForms.Checked;
        _radioValue = field.Kind == FormFieldKind.Radio ? field.Value : null;
    }

    /// <summary>A field drawn in the editor.</summary>
    public FormFieldViewModel(string name, FormFieldKind kind, int page, PdfRect rect)
    {
        _name = name;
        _kind = kind;
        Options = [];
        IsAdded = true;
        Widgets = [new FormWidget(page, rect, null)];
        _text = "";
    }

    public bool IsAdded { get; }

    public bool ReadOnly { get; }

    public IReadOnlyList<string> Options { get; }

    /// <summary>Every place the field shows. An added field has one, which can be moved and resized.</summary>
    public List<FormWidget> Widgets { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsText), nameof(IsCheckBox))]
    private FormFieldKind _kind;

    [ObservableProperty]
    private bool _required;

    /// <summary>In points; 0 = automatic.</summary>
    [ObservableProperty]
    private double _fontSize;

    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    private bool _isChecked;

    [ObservableProperty]
    private string? _radioValue;

    public bool IsText => Kind is FormFieldKind.Text or FormFieldKind.MultilineText;

    public bool IsCheckBox => Kind == FormFieldKind.CheckBox;

    public bool IsSupported => Kind is not (FormFieldKind.Signature or FormFieldKind.Unsupported);

    public bool IsFilled => Kind switch
    {
        FormFieldKind.CheckBox => IsChecked,
        FormFieldKind.Radio => !string.IsNullOrEmpty(RadioValue),
        _ => !string.IsNullOrWhiteSpace(Text),
    };

    /// <summary>The value written to the PDF (see <see cref="PdfForms"/>).</summary>
    public string Value => Kind switch
    {
        FormFieldKind.CheckBox => IsChecked ? PdfForms.Checked : PdfForms.Unchecked,
        FormFieldKind.Radio => RadioValue ?? "Off",
        _ => Text,
    };

    public NewFormField ToNewField() =>
        new(Name, Kind, Widgets[0].Page, Widgets[0].Rect, FontSize, Required, IsFilled ? Value : null);
}

/// <summary>A page in the left column.</summary>
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
/// Form editor: the page drawn by PDFium with a control over each field. "Fill" mode types the
/// answers; "Add fields" mode draws new text fields and check boxes with the mouse. Saving queues
/// <see cref="PdfFormFillTool"/>, optionally flattening the form.
/// </summary>
public partial class PdfFormEditorViewModel : PageViewModel, IDisposable
{
    /// <summary>Display units (DIP) per PDF point at 100 % zoom.</summary>
    public const double Scale = 1.5;

    private const int ThumbnailSize = 150;

    private readonly MainWindowViewModel _main;
    private readonly AppServices _services;
    private readonly PdfFormFillTool _tool;
    private readonly PageViewModel _back;
    private readonly CancellationTokenSource _cancellation = new();
    private PdfRenderer? _renderer;
    private IReadOnlyList<PdfPageGeometry> _geometry = [];

    public PdfFormEditorViewModel(MainWindowViewModel main, AppServices services, PdfFormFillTool tool, string file, PageViewModel back)
    {
        _main = main;
        _services = services;
        _tool = tool;
        _back = back;
        FilePath = file;
        FileName = Path.GetFileName(file);
        _selectedNewKind = NewKinds[0];
        _ = LoadAsync();
    }

    public string FilePath { get; }

    public string FileName { get; }

    public ObservableCollection<FormPageViewModel> Pages { get; } = [];

    public ObservableCollection<FormFieldViewModel> Fields { get; } = [];

    public IReadOnlyList<FieldKindChoice> NewKinds { get; } =
    [
        new(FormFieldKind.Text, Strings.Form_Text),
        new(FormFieldKind.MultilineText, Strings.Form_Multiline),
        new(FormFieldKind.CheckBox, Strings.Form_CheckBox),
    ];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
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

    /// <summary>True in "Add fields" mode.</summary>
    [ObservableProperty]
    private bool _isAdding;

    [ObservableProperty]
    private FieldKindChoice _selectedNewKind;

    /// <summary>The added field being edited (name, type, size…).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private FormFieldViewModel? _selectedField;

    [ObservableProperty]
    private string? _fieldError;

    [ObservableProperty]
    private bool _flatten;

    [ObservableProperty]
    private double _zoom = 1;

    /// <summary>Raised when the fields of the current page must be laid out again.</summary>
    public event EventHandler? LayoutChanged;

    public bool HasSelection => SelectedField is not null;

    public string PageLabel => string.Format(Strings.Form_PageOf, CurrentPage + 1, Math.Max(1, Pages.Count));

    public double PageWidth => DisplaySize(CurrentPage).Width;

    public double PageHeight => DisplaySize(CurrentPage).Height;

    public string Summary => string.Format(Strings.Form_Summary, Fields.Count(f => f.IsSupported), Fields.Count(f => f.IsSupported && f.IsFilled));

    public bool HasNoFields => !IsLoading && Error is null && Fields.Count == 0;

    // ---- Loading -------------------------------------------------------------------------------

    private async Task LoadAsync()
    {
        try
        {
            var (fields, geometry, xfa) = await Task.Run(() =>
            {
                using var document = PdfForms.Open(FilePath);
                return (PdfForms.Read(document), PdfForms.Pages(document), PdfForms.IsDynamicXfa(document));
            });
            if (xfa)
                throw new ToolException(Strings.Error_FormUnsupported);
            _geometry = geometry;
            _renderer = await Task.Run(() => PdfRenderer.Open(FilePath));
            foreach (var field in fields)
                Track(new FormFieldViewModel(field));
            for (var i = 0; i < _renderer.PageCount; i++)
                Pages.Add(new FormPageViewModel(i));
        }
        catch (ToolException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException)
        {
            Error = Strings.Error_PdfUnreadable;
        }
        IsLoading = false;
        OnPropertyChanged(nameof(HasNoFields));
        if (Error is not null || Pages.Count == 0)
            return;

        // A PDF without fields opens straight in "Add fields" mode.
        IsAdding = Fields.Count == 0;
        await ShowPageAsync(0);
        await RenderThumbnailsAsync();
    }

    private void Track(FormFieldViewModel field)
    {
        field.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(FormFieldViewModel.Text) or nameof(FormFieldViewModel.IsChecked) or nameof(FormFieldViewModel.RadioValue))
                OnPropertyChanged(nameof(Summary));
        };
        Fields.Add(field);
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasNoFields));
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

    private async Task ShowPageAsync(int index)
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
        LayoutChanged?.Invoke(this, EventArgs.Empty);
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

    // ---- Adding fields -------------------------------------------------------------------------

    /// <summary>A rectangle drawn on the current page (display units) becomes a new field.</summary>
    public void AddField(Rect displayRect)
    {
        var kind = SelectedNewKind.Kind;
        // Check boxes are square, whatever was drawn.
        if (kind == FormFieldKind.CheckBox)
        {
            var side = Math.Clamp(Math.Min(displayRect.Width, displayRect.Height), 12 * Scale, 24 * Scale);
            displayRect = new Rect(displayRect.X, displayRect.Y, side, side);
        }
        var field = new FormFieldViewModel(UniqueName(), kind, CurrentPage, FromDisplay(CurrentPage, displayRect));
        Track(field);
        SelectedField = field;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves or resizes an added field (display units).</summary>
    public void MoveField(FormFieldViewModel field, Rect displayRect)
    {
        if (!field.IsAdded)
            return;
        field.Widgets[0] = field.Widgets[0] with { Rect = FromDisplay(field.Widgets[0].Page, displayRect) };
    }

    private string UniqueName()
    {
        for (var n = 1; ; n++)
        {
            var name = string.Format(Strings.Form_DefaultName, n);
            if (Fields.All(f => f.Name != name))
                return name;
        }
    }

    partial void OnSelectedFieldChanged(FormFieldViewModel? value) => FieldError = null;

    partial void OnIsAddingChanged(bool value)
    {
        SelectedField = null;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void FillMode() => IsAdding = false;

    [RelayCommand]
    private void AddMode() => IsAdding = true;

    [RelayCommand]
    private void DeleteSelectedField()
    {
        if (SelectedField is not { IsAdded: true } field)
            return;
        Fields.Remove(field);
        SelectedField = null;
        OnPropertyChanged(nameof(Summary));
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Field names must be unique (the PDF finds fields by name).</summary>
    public bool RenameSelected(string name)
    {
        if (SelectedField is not { } field)
            return false;
        name = name.Trim();
        if (name.Length == 0 || Fields.Any(f => f != field && f.Name == name))
        {
            FieldError = string.Format(Strings.Form_NameTaken, name);
            return false;
        }
        FieldError = null;
        field.Name = name;
        return true;
    }

    // ---- Navigation and saving -----------------------------------------------------------------

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
    private void ZoomOut() => Zoom = Math.Max(0.5, Math.Round(Zoom - 0.25, 2));

    private bool CanSave() => !IsLoading && Error is null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        var settings = ToolSettings.FromDefaults(_tool);
        settings[PdfFormFillTool.ValuesKey] = PdfFormFillTool.SerializeValues(
            Fields.Where(f => !f.IsAdded && f.IsSupported && !f.ReadOnly).ToDictionary(f => f.Name, f => f.Value));
        settings[PdfFormFillTool.AddedKey] = PdfFormFillTool.SerializeAdded(Fields.Where(f => f.IsAdded).Select(f => f.ToNewField()).ToList());
        settings[PdfFormFillTool.FlattenKey] = Flatten;
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
        _renderer?.Dispose();
        _renderer = null;
    }
}
