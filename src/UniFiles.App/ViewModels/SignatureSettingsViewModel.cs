using System.Collections.ObjectModel;
using System.Security.Cryptography.X509Certificates;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniFiles.App.Services;
using UniFiles.Core.Resources;
using UniFiles.Core.Signing;
using UniFiles.Core.Tools;

namespace UniFiles.App.ViewModels;

/// <summary>A saved signature: the certificate and its handwritten signature, if any.</summary>
public sealed class SignatureItemViewModel
{
    public SignatureItemViewModel(SigningIdentity identity)
    {
        Identity = identity;
        Drawing = SigningContext.Handwriting.Drawing(identity.Id);
        Image = SigningContext.Handwriting.ImagePath(identity.Id) is { } path ? new Bitmap(path) : null;
    }

    public SigningIdentity Identity { get; }

    public Handwriting? Drawing { get; }

    public Bitmap? Image { get; }

    public bool HasDrawing => Drawing is not null;

    public bool HasImage => Image is not null;

    public bool HasNoHandwriting => !HasDrawing && !HasImage;

    public string Name => Identity.Name;

    public string? Email => Identity.Email;

    public bool HasEmail => Email is not null;

    public string Detail => Identity.IsValidNow
        ? string.Format(Strings.SignSettings_IssuedBy, Identity.IsSelfSigned ? Strings.SignSettings_Self : Identity.Issuer, Identity.NotAfter)
        : string.Format(Strings.Error_CertificateExpired, Identity.NotAfter);

    public bool IsQualified => Identity.DeclaredQualified;

    public bool IsHardware => Identity.IsHardware;

    public bool IsPersonal => Identity.CreatedByUniFiles && Identity.IsSelfSigned;

    public bool ExpiresSoon => Identity.ExpiresSoon;

    public bool IsExpired => !Identity.IsValidNow;
}

public sealed class AuthorityItemViewModel(OrganizationAuthority authority)
{
    public OrganizationAuthority Authority { get; } = authority;

    public string Name => Authority.Name;

    public string Detail => string.Format(Strings.SignSettings_AuthorityDetail, Authority.Issued.Count switch
    {
        0 => Strings.SignSettings_IssuedNone,
        1 => Strings.SignSettings_IssuedOne,
        var n => string.Format(Strings.SignSettings_IssuedMany, n),
    }, Authority.Certificate.NotAfter);
}

public sealed class TrustedItemViewModel(X509Certificate2 certificate, bool own)
{
    public X509Certificate2 Certificate { get; } = certificate;

    public string Name => Certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);

    /// <summary>The user's own authorities are trusted while they exist; only accepted ones can be removed.</summary>
    public bool CanRemove { get; } = !own;
}

/// <summary>
/// Preferences › Signatures: the saved signatures, with "Create a signature" (step by step) and
/// "Import"; a signature opens its details. Organization authority, trusted authorities and
/// time-stamping are tucked under "Advanced options".
/// </summary>
public sealed partial class SignatureSettingsViewModel : PageViewModel
{
    private readonly MainWindowViewModel _main;
    private readonly AppServices _services;

    public SignatureSettingsViewModel(MainWindowViewModel main, AppServices services)
    {
        _main = main;
        _services = services;
        _timestamp = services.Settings.Signing.Timestamp;
        _timestampUrl = services.Settings.Signing.TimestampUrl;
    }

    private static IdentityStore Identities => SigningContext.Identities;

    /// <summary>The signing screen that opened this page, to go back to it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReturn))]
    private PageViewModel? _returnTo;

    public bool CanReturn => ReturnTo is not null;

    [RelayCommand]
    private void Return()
    {
        if (ReturnTo is { } page)
        {
            ReturnTo = null;
            _main.ShowPage(page);
        }
    }

    public ObservableCollection<SignatureItemViewModel> Items { get; } = [];

    public ObservableCollection<AuthorityItemViewModel> Authorities { get; } = [];

    public ObservableCollection<TrustedItemViewModel> Trusted { get; } = [];

    public bool HasNoSignature => Items.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string? _notice;

    [ObservableProperty]
    private bool _noticeIsError;

    public bool HasNotice => Notice is not null;

    internal void Tell(string? text, bool error = false)
    {
        Notice = text;
        NoticeIsError = error;
    }

    public void Refresh()
    {
        foreach (var item in Items)
            item.Image?.Dispose();
        Items.Clear();
        foreach (var identity in Identities.Identities())
            Items.Add(new SignatureItemViewModel(identity));
        Authorities.Clear();
        foreach (var authority in Identities.Authorities())
            Authorities.Add(new AuthorityItemViewModel(authority));
        Trusted.Clear();
        var own = Identities.Authorities().Select(a => a.Id).ToHashSet();
        foreach (var certificate in Identities.TrustedAuthorities())
            Trusted.Add(new TrustedItemViewModel(certificate, own.Contains(IdentityStore.IdOf(certificate))));
        OnPropertyChanged(nameof(HasNoSignature));
        OnPropertyChanged(nameof(HasAuthorities));
        OnPropertyChanged(nameof(HasTrusted));
    }

    /// <summary>Back to this list (from creation or details), the sidebar unchanged.</summary>
    internal void Show(string? notice = null)
    {
        Refresh();
        Tell(notice);
        _main.ShowInPreferences(this);
    }

    // ---- Signatures -------------------------------------------------------------------------------

    [RelayCommand]
    private void Create()
    {
        Tell(null);
        _main.ShowInPreferences(new SignatureWizardViewModel(this, _services));
    }

    [RelayCommand]
    private void Open(SignatureItemViewModel item)
    {
        Tell(null);
        _main.ShowInPreferences(new SignatureDetailViewModel(this, item.Identity));
    }

    /// <summary>A .p12 waiting for its password.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImporting), nameof(ImportFileName))]
    private string? _importPath;

    [ObservableProperty]
    private string _importPassword = "";

    public bool IsImporting => ImportPath is not null;

    public string ImportFileName => ImportPath is null ? "" : Path.GetFileName(ImportPath);

    [RelayCommand]
    private async Task PickImport()
    {
        if (await DialogService.PickFileAsync(Strings.SignSettings_Import, Strings.SignSettings_P12Type, ["*.p12", "*.pfx"]) is { } path)
        {
            ImportPath = path;
            ImportPassword = "";
        }
    }

    [RelayCommand]
    private void ConfirmImport()
    {
        if (ImportPath is not { } path)
            return;
        try
        {
            var identity = Identities.Import(File.ReadAllBytes(path), ImportPassword);
            ImportPath = null;
            // Straight to its details, to add a handwritten signature.
            _main.ShowInPreferences(new SignatureDetailViewModel(this, identity, string.Format(Strings.SignSettings_Imported, identity.Name)));
        }
        catch (ToolException ex)
        {
            Tell(ex.Message, error: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Tell(Strings.SignSettings_FileUnreadable, error: true);
        }
        finally
        {
            ImportPassword = "";
        }
    }

    [RelayCommand]
    private void CancelImport()
    {
        ImportPath = null;
        ImportPassword = "";
    }

    // ---- Advanced: organization ------------------------------------------------------------------------

    [ObservableProperty]
    private bool _showAdvanced;

    [RelayCommand]
    private void ToggleAdvanced() => ShowAdvanced = !ShowAdvanced;

    public bool HasAuthorities => Authorities.Count > 0;

    public bool HasTrusted => Trusted.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateAuthorityCommand))]
    private string _organization = "";

    private bool CanCreateAuthority() => Organization.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanCreateAuthority))]
    private void CreateAuthority()
    {
        var authority = Identities.CreateAuthority(Organization.Trim());
        Organization = "";
        Refresh();
        Tell(string.Format(Strings.SignSettings_AuthorityCreated, authority.Name));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIssuing))]
    private AuthorityItemViewModel? _issuing;

    public bool IsIssuing => Issuing is not null;

    [ObservableProperty]
    private string _memberName = "";

    [ObservableProperty]
    private string _memberEmail = "";

    [ObservableProperty]
    private string _memberPassword = "";

    [RelayCommand]
    private void StartIssue(AuthorityItemViewModel authority)
    {
        Issuing = authority;
        MemberName = MemberEmail = MemberPassword = "";
    }

    [RelayCommand]
    private void CancelIssue() => Issuing = null;

    [RelayCommand]
    private async Task Issue()
    {
        if (Issuing is not { } authority)
            return;
        if (MemberName.Trim().Length == 0 || MemberPassword.Length < 8)
        {
            Tell(Strings.SignSettings_IssueIncomplete, error: true);
            return;
        }
        if (await DialogService.SaveFileAsync(Strings.SignSettings_Issue, $"{MemberName.Trim()}.p12", Strings.SignSettings_P12Type, "p12") is not { } path)
            return;
        var p12 = Identities.Issue(authority.Authority, MemberName.Trim(), string.IsNullOrWhiteSpace(MemberEmail) ? null : MemberEmail.Trim(), MemberPassword);
        await File.WriteAllBytesAsync(path, p12);
        var cer = Path.Combine(Path.GetDirectoryName(path)!, $"{authority.Name}.cer");
        await File.WriteAllBytesAsync(cer, IdentityStore.ExportPublic(authority.Authority.Certificate));
        Issuing = null;
        MemberPassword = "";
        Refresh();
        Tell(string.Format(Strings.SignSettings_Issued, Path.GetFileName(path), Path.GetFileName(cer)));
        await DialogService.ShowAsync(path);
    }

    [RelayCommand]
    private async Task ExportAuthority(AuthorityItemViewModel authority)
    {
        if (await DialogService.SaveFileAsync(Strings.SignSettings_Export, $"{authority.Name}.cer", Strings.SignSettings_CerType, "cer") is { } path)
        {
            await File.WriteAllBytesAsync(path, IdentityStore.ExportPublic(authority.Authority.Certificate));
            Tell(Strings.SignSettings_Exported);
        }
    }

    [RelayCommand]
    private async Task TrustAuthority()
    {
        if (await DialogService.PickFileAsync(Strings.SignSettings_Trust, Strings.SignSettings_CerType, ["*.cer", "*.crt", "*.pem", "*.der"]) is not { } path)
            return;
        try
        {
            var certificate = Identities.Trust(await File.ReadAllBytesAsync(path));
            Refresh();
            Tell(string.Format(Strings.SignSettings_Trusted, certificate.GetNameInfo(X509NameType.SimpleName, false)));
        }
        catch (ToolException ex)
        {
            Tell(ex.Message, error: true);
        }
    }

    [RelayCommand]
    private void Untrust(TrustedItemViewModel item)
    {
        Identities.Untrust(item.Certificate);
        Refresh();
    }

    // ---- Advanced: time stamp --------------------------------------------------------------------------

    [ObservableProperty]
    private bool _timestamp;

    [ObservableProperty]
    private string _timestampUrl;

    partial void OnTimestampChanged(bool value)
    {
        _services.Settings.Signing.Timestamp = value;
        _services.SaveSettings();
    }

    partial void OnTimestampUrlChanged(string value)
    {
        _services.Settings.Signing.TimestampUrl = value.Trim();
        _services.SaveSettings();
    }
}
