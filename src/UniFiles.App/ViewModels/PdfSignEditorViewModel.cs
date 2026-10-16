using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniFiles.App.Services;
using UniFiles.Core.Resources;
using UniFiles.Core.Signing;
using UniFiles.Core.Tools;
using UniFiles.Core.Tools.Pdf;

namespace UniFiles.App.ViewModels;

/// <summary>A signature field of the PDF: empty (click to sign there) or already signed.</summary>
public sealed class SignatureFieldViewModel(string name, int page, PdfRect rect, string? signedBy)
{
    public string Name { get; } = name;

    public int Page { get; } = page;

    public PdfRect Rect { get; } = rect;

    public string? SignedBy { get; } = signedBy;

    public bool IsEmpty => SignedBy is null;
}

public sealed record IdentityChoice(SigningIdentity Identity)
{
    public override string ToString() => Identity.IsSelfSigned
        ? $"{Identity.Name} · {Strings.SignSettings_BadgePersonal}"
        : $"{Identity.Name} · {Identity.Issuer}";
}

public sealed record HandwritingChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// "Sign a PDF": the page with its signature fields; click an empty field or draw a box (or choose
/// an invisible signature), pick the certificate and what the signature shows, then sign. The
/// signature itself runs in the queue (<see cref="PdfSignTool"/>).
/// </summary>
public sealed partial class PdfSignEditorViewModel : PdfPagesViewModel, IShownAgain
{
    private readonly PdfSignTool _tool;

    public PdfSignEditorViewModel(MainWindowViewModel main, AppServices services, PdfSignTool tool, string file, PageViewModel back)
        : base(main, services, file, back)
    {
        _tool = tool;
        var saved = services.Settings.Signing;
        _showName = saved.ShowName;
        _showDate = saved.ShowDate;
        _showReason = saved.ShowReason;
        _reason = saved.Reason;
        _location = saved.Location;
        _useTimestamp = saved.Timestamp && CanTimestamp;
        RefreshIdentities();
        RefreshHandwriting(saved.Handwriting);
        _ = LoadAsync();
    }

    public ObservableCollection<SignatureFieldViewModel> Fields { get; } = [];

    /// <summary>"Already signed by Léo Dupont, Zoé Bernard", or null.</summary>
    [ObservableProperty]
    private string? _existingSignatures;

    /// <summary>The PDF has form fields still editable (and isn't signed yet): offer to flatten them first.</summary>
    [ObservableProperty]
    private bool _hasEditableFields;

    [ObservableProperty]
    private bool _flatten = true;

    // ---- Loading -------------------------------------------------------------------------------

    private async Task LoadAsync()
    {
        try
        {
            var (fields, geometry, xfa, signed, editable, display) = await Task.Run(() =>
            {
                var bytes = File.ReadAllBytes(FilePath);
                using var document = PdfForms.Open(FilePath);
                var reports = PdfSignatureVerifier.Verify(bytes, SigningContext.Identities.TrustedAuthorities());
                var all = PdfForms.Read(document);
                // What is signed must be seen: values and signatures drawn into a copy for display.
                var copy = all.Count > 0 && !PdfForms.IsDynamicXfa(document) ? PdfForms.DisplayCopy(FilePath) : null;
                return (all, PdfForms.Pages(document), PdfForms.IsDynamicXfa(document), reports,
                    all.Any(f => f.Kind is not (FormFieldKind.Signature or FormFieldKind.Unsupported) && !f.ReadOnly), copy);
            });
            if (xfa)
                throw new ToolException(Strings.Error_FormUnsupported);
            await OpenPagesAsync(geometry, display);
            foreach (var signature in fields.Where(f => f.Kind == FormFieldKind.Signature))
            {
                var by = signed.FirstOrDefault(r => r.FieldName == signature.Name)?.SignerName;
                foreach (var widget in signature.Widgets.Count > 0 ? signature.Widgets : [new FormWidget(signature.Page, signature.Rect, null)])
                    if (widget.Rect.Width > 1 && widget.Rect.Height > 1)
                        Fields.Add(new SignatureFieldViewModel(signature.Name, widget.Page, widget.Rect, by));
            }
            if (signed.Count > 0)
                ExistingSignatures = string.Format(Strings.Sign_AlreadySigned, string.Join(", ", signed.Select(r => r.SignerName).Distinct()));
            HasEditableFields = editable && signed.Count == 0;
            // An empty field is where the signature is expected: chosen from the start.
            if (Fields.FirstOrDefault(f => f.IsEmpty) is { } first)
                Placement = new SignaturePlacement.ExistingField(first.Name);
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
        var startPage = Placement is SignaturePlacement.ExistingField chosen && Fields.FirstOrDefault(f => f.Name == chosen.Name) is { } shown ? shown.Page : 0;
        await ShowPagesAsync(startPage);
    }

    protected override void OnStateChanged() => SignCommand.NotifyCanExecuteChanged();

    // ---- Placement ------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInvisible), nameof(PlacementText))]
    [NotifyCanExecuteChangedFor(nameof(SignCommand))]
    private SignaturePlacement? _placement;

    public bool IsInvisible
    {
        get => Placement is SignaturePlacement.Invisible;
        set => Placement = value ? new SignaturePlacement.Invisible() : null;
    }

    public string PlacementText => Placement switch
    {
        SignaturePlacement.Invisible => Strings.Sign_PlacementInvisible,
        SignaturePlacement.ExistingField chosen => string.Format(Strings.Sign_PlacementField, chosen.Name),
        SignaturePlacement.NewBox box => string.Format(Strings.Sign_PlacementBox, box.Page + 1),
        _ => Strings.Sign_PlacementNone,
    };

    partial void OnPlacementChanged(SignaturePlacement? value) => RaiseLayoutChanged();

    public void ChooseField(SignatureFieldViewModel field)
    {
        if (field.IsEmpty)
            Placement = new SignaturePlacement.ExistingField(field.Name);
    }

    /// <summary>A box drawn on the current page (display units).</summary>
    public void DrawBox(Rect displayRect) => Placement = new SignaturePlacement.NewBox(CurrentPage, FromDisplay(CurrentPage, displayRect));

    /// <summary>Where the signature shows on the current page, in display units (for the preview).</summary>
    public Rect? PlacementOnPage => Placement switch
    {
        SignaturePlacement.NewBox box when box.Page == CurrentPage => ToDisplay(box.Page, box.Rect),
        SignaturePlacement.ExistingField chosen when Fields.FirstOrDefault(f => f.Name == chosen.Name && f.Page == CurrentPage) is { } shown => ToDisplay(shown.Page, shown.Rect),
        _ => (Rect?)null,
    };

    // ---- Identity -------------------------------------------------------------------------------

    public ObservableCollection<IdentityChoice> Identities { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IdentityText))]
    [NotifyCanExecuteChangedFor(nameof(SignCommand))]
    private IdentityChoice? _selectedIdentity;

    public bool HasNoIdentity => Identities.Count == 0;

    /// <summary>"Signed by Élodie Martin · certificate from Certigna · valid until 12/03/2028".</summary>
    public string IdentityText => SelectedIdentity?.Identity is not { } identity ? ""
        : string.Format(Strings.Sign_IdentitySummary, identity.Name,
            identity.IsSelfSigned ? Strings.SignSettings_Self : identity.Issuer, identity.NotAfter)
          + (identity.DeclaredQualified ? " · " + Strings.SignSettings_BadgeQualified : "");

    private void RefreshIdentities()
    {
        var selected = SelectedIdentity?.Identity.Id ?? Services.Settings.Signing.LastIdentity;
        Identities.Clear();
        foreach (var identity in SigningContext.Identities.Identities().Where(i => i.CanSignNow))
            Identities.Add(new IdentityChoice(identity));
        SelectedIdentity = Identities.FirstOrDefault(i => i.Identity.Id == selected) ?? Identities.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoIdentity));
    }

    [RelayCommand]
    private void ManageCertificates() => Main.OpenSignatureSettings(this);

    /// <summary>Back from the signature settings: new certificates, new handwritten signature.</summary>
    public void OnShownAgain()
    {
        RefreshIdentities();
        RefreshHandwriting(SelectedHandwriting?.Value ?? Services.Settings.Signing.Handwriting);
        RaiseLayoutChanged();
    }

    // ---- What the signature shows ---------------------------------------------------------------------

    [ObservableProperty]
    private bool _showName;

    [ObservableProperty]
    private bool _showDate;

    [ObservableProperty]
    private bool _showReason;

    [ObservableProperty]
    private string _reason;

    [ObservableProperty]
    private string _location;

    public IReadOnlyList<string> SuggestedReasons { get; } = [Strings.Sign_ReasonRead, Strings.Sign_ReasonAgreed];

    [RelayCommand]
    private void UseReason(string reason) => Reason = reason;

    public ObservableCollection<HandwritingChoice> HandwritingChoices { get; } = [];

    [ObservableProperty]
    private HandwritingChoice? _selectedHandwriting;

    /// <summary>The handwritten signature of the chosen certificate, or none (the user's last choice is kept).</summary>
    private void RefreshHandwriting(string preferred)
    {
        HandwritingChoices.Clear();
        HandwritingChoices.Add(new HandwritingChoice("none", Strings.Sign_HandwritingNone));
        var kind = SelectedIdentity is { } identity ? SigningContext.Handwriting.KindOf(identity.Identity.Id) : "none";
        if (kind != "none")
            HandwritingChoices.Add(new HandwritingChoice(kind, kind == "drawn" ? Strings.Sign_HandwritingDrawn : Strings.Sign_HandwritingImage));
        SelectedHandwriting = preferred == "none" ? HandwritingChoices[0] : HandwritingChoices[^1];
    }

    /// <summary>The lines of the preview, as the signature will show them.</summary>
    public IReadOnlyList<string> PreviewLines
    {
        get
        {
            var lines = new List<string>();
            if (ShowName && SelectedIdentity is { } identity)
                lines.Add(identity.Identity.Name);
            if (ShowDate)
                lines.Add(DateTime.Now.ToString("g", CultureInfo.CurrentCulture));
            if (ShowReason && !string.IsNullOrWhiteSpace(Reason))
                lines.Add(Reason);
            return lines;
        }
    }

    partial void OnShowNameChanged(bool value) => RaiseLayoutChanged();

    partial void OnShowDateChanged(bool value) => RaiseLayoutChanged();

    partial void OnShowReasonChanged(bool value) => RaiseLayoutChanged();

    partial void OnReasonChanged(string value) => RaiseLayoutChanged();

    partial void OnSelectedIdentityChanged(IdentityChoice? value)
    {
        RefreshHandwriting(Services.Settings.Signing.Handwriting);
        RaiseLayoutChanged();
    }

    partial void OnSelectedHandwritingChanged(HandwritingChoice? value) => RaiseLayoutChanged();

    // ---- Time stamp ---------------------------------------------------------------------------------

    public bool CanTimestamp => Uri.TryCreate(Services.Settings.Signing.TimestampUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    [ObservableProperty]
    private bool _useTimestamp;

    // ---- Signing --------------------------------------------------------------------------------------

    private bool CanSign() => !IsLoading && Error is null && Placement is not null && SelectedIdentity is not null;

    [RelayCommand(CanExecute = nameof(CanSign))]
    private void Sign()
    {
        if (Placement is not { } placement || SelectedIdentity is not { } identity)
            return;

        var saved = Services.Settings.Signing;
        saved.LastIdentity = identity.Identity.Id;
        saved.ShowName = ShowName;
        saved.ShowDate = ShowDate;
        saved.ShowReason = ShowReason;
        saved.Reason = Reason.Trim();
        saved.Location = Location.Trim();
        saved.Handwriting = SelectedHandwriting?.Value ?? "none";
        Services.SaveSettings();

        var settings = ToolSettings.FromDefaults(_tool);
        settings[PdfSignTool.IdentityKey] = identity.Identity.Id;
        settings[PdfSignTool.PlacementKey] = PdfSignTool.Placement(placement);
        settings[PdfSignTool.ReasonKey] = Reason.Trim();
        settings[PdfSignTool.LocationKey] = Location.Trim();
        settings[PdfSignTool.ShowNameKey] = ShowName;
        settings[PdfSignTool.ShowDateKey] = ShowDate;
        settings[PdfSignTool.ShowReasonKey] = ShowReason;
        settings[PdfSignTool.HandwritingKey] = saved.Handwriting;
        settings[PdfSignTool.TimestampKey] = UseTimestamp && CanTimestamp ? saved.TimestampUrl : "";
        settings[PdfSignTool.FlattenKey] = HasEditableFields && Flatten;
        Services.Queue.Enqueue(_tool, [InputFile.Of(FilePath)], settings, Services.TargetFor(settings));
        Finish();
    }
}
