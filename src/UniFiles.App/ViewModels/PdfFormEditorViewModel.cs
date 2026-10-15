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

/// <summary>
/// Form editor: the page drawn by PDFium with a control over each field. "Fill" mode types the
/// answers; "Add fields" mode draws new text fields and check boxes with the mouse. Saving queues
/// <see cref="PdfFormFillTool"/>, optionally flattening the form.
/// </summary>
public partial class PdfFormEditorViewModel : PdfPagesViewModel
{
    private readonly PdfFormFillTool _tool;

    public PdfFormEditorViewModel(MainWindowViewModel main, AppServices services, PdfFormFillTool tool, string file, PageViewModel back)
        : base(main, services, file, back)
    {
        _tool = tool;
        _selectedNewKind = NewKinds[0];
        _ = LoadAsync();
    }

    public ObservableCollection<FormFieldViewModel> Fields { get; } = [];

    public IReadOnlyList<FieldKindChoice> NewKinds { get; } =
    [
        new(FormFieldKind.Text, Strings.Form_Text),
        new(FormFieldKind.MultilineText, Strings.Form_Multiline),
        new(FormFieldKind.CheckBox, Strings.Form_CheckBox),
        new(FormFieldKind.Signature, Strings.Form_Signature),
    ];

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

    public bool HasSelection => SelectedField is not null;

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
            await OpenPagesAsync(geometry);
            foreach (var field in fields)
                Track(new FormFieldViewModel(field));
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
        await ShowPagesAsync();
    }

    protected override void OnStateChanged() => SaveCommand.NotifyCanExecuteChanged();

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
        var field = new FormFieldViewModel(UniqueName(kind), kind, CurrentPage, FromDisplay(CurrentPage, displayRect));
        Track(field);
        SelectedField = field;
        RaiseLayoutChanged();
    }

    /// <summary>Moves or resizes an added field (display units).</summary>
    public void MoveField(FormFieldViewModel field, Rect displayRect)
    {
        if (!field.IsAdded)
            return;
        field.Widgets[0] = field.Widgets[0] with { Rect = FromDisplay(field.Widgets[0].Page, displayRect) };
    }

    private string UniqueName(FormFieldKind kind)
    {
        for (var n = 1; ; n++)
        {
            var name = kind == FormFieldKind.Signature ? $"signature_{n}" : string.Format(Strings.Form_DefaultName, n);
            if (Fields.All(f => f.Name != name))
                return name;
        }
    }

    partial void OnSelectedFieldChanged(FormFieldViewModel? value) => FieldError = null;

    partial void OnIsAddingChanged(bool value)
    {
        SelectedField = null;
        RaiseLayoutChanged();
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
        RaiseLayoutChanged();
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

    private bool CanSave() => !IsLoading && Error is null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        var settings = ToolSettings.FromDefaults(_tool);
        settings[PdfFormFillTool.ValuesKey] = PdfFormFillTool.SerializeValues(
            Fields.Where(f => !f.IsAdded && f.IsSupported && !f.ReadOnly).ToDictionary(f => f.Name, f => f.Value));
        settings[PdfFormFillTool.AddedKey] = PdfFormFillTool.SerializeAdded(Fields.Where(f => f.IsAdded).Select(f => f.ToNewField()).ToList());
        settings[PdfFormFillTool.FlattenKey] = Flatten;
        Services.Queue.Enqueue(_tool, [InputFile.Of(FilePath)], settings, Services.TargetFor(settings));
        Finish();
    }
}
