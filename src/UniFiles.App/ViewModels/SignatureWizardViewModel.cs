using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniFiles.App.Services;
using UniFiles.Core.Resources;
using UniFiles.Core.Signing;
using UniFiles.Core.Tools;

namespace UniFiles.App.ViewModels;

/// <summary>
/// Creating a signature, one question at a time: first and last name, the handwritten signature,
/// then the email (optional). The certificate is made at the end, with the handwriting attached.
/// </summary>
public sealed partial class SignatureWizardViewModel(SignatureSettingsViewModel owner, AppServices services) : PageViewModel
{
    public const int StepCount = 3;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNameStep), nameof(IsHandwritingStep), nameof(IsEmailStep), nameof(StepText), nameof(ContinueLabel), nameof(Step1Done), nameof(Step2Done), nameof(Step3Done))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    private int _step = 1;

    public bool IsNameStep => Step == 1;

    public bool IsHandwritingStep => Step == 2;

    public bool IsEmailStep => Step == 3;

    // Progress dots: done or current.
    public bool Step1Done => Step >= 1;

    public bool Step2Done => Step >= 2;

    public bool Step3Done => Step >= 3;

    public string StepText => string.Format(Strings.Wizard_StepOf, Step, StepCount);

    public string ContinueLabel => Step == StepCount ? Strings.Wizard_Create : Strings.Wizard_Continue;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    private string _firstName = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    private string _lastName = "";

    [ObservableProperty]
    private string _email = "";

    public string FullName => $"{FirstName.Trim()} {LastName.Trim()}".Trim();

    /// <summary>The drawing on the pad, kept between steps.</summary>
    public Handwriting? Drawing { get; set; }

    /// <summary>Or an image picked instead (path of the original).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private Bitmap? _image;

    private string? _imagePath;

    public bool HasImage => Image is not null;

    [ObservableProperty]
    private string? _error;

    private bool CanContinue() => Step != 1 || (FirstName.Trim().Length > 0 && LastName.Trim().Length > 0);

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private void Continue()
    {
        Error = null;
        if (Step < StepCount)
        {
            Step++;
            return;
        }

        var email = string.IsNullOrWhiteSpace(Email) ? null : Email.Trim();
        if (email is not null && !email.Contains('@'))
        {
            Error = Strings.Wizard_EmailInvalid;
            return;
        }
        var identity = SigningContext.Identities.CreatePersonal(FullName, email);
        try
        {
            if (_imagePath is not null)
                SigningContext.Handwriting.ImportImage(identity.Id, _imagePath);
            else if (Drawing is { IsEmpty: false } drawing)
                SigningContext.Handwriting.SaveDrawing(identity.Id, drawing);
        }
        catch (ToolException)
        {
            // The certificate is made; the handwriting can be added from its details.
        }
        services.Settings.Signing.LastIdentity = identity.Id;
        services.SaveSettings();
        Image?.Dispose();
        owner.Show(string.Format(Strings.SignSettings_Created, identity.Name));
    }

    [RelayCommand]
    private void Back()
    {
        Error = null;
        if (Step > 1)
            Step--;
        else
            Cancel();
    }

    [RelayCommand]
    private void Cancel()
    {
        Image?.Dispose();
        owner.Show();
    }

    [RelayCommand]
    private async Task PickImage()
    {
        if (await DialogService.PickFileAsync(Strings.SignSettings_HandwritingImport, Strings.SignSettings_ImageType, ["*.png", "*.jpg", "*.jpeg", "*.heic", "*.webp"]) is not { } path)
            return;
        // Previewed as prepared (transparent around the ink) in a scratch store, applied at the end.
        try
        {
            var scratch = new HandwritingStore(Path.Combine(Path.GetTempPath(), "unifiles-wizard"));
            scratch.ImportImage("preview", path);
            Image?.Dispose();
            Image = new Bitmap(scratch.ImagePath("preview")!);
            _imagePath = path;
            Drawing = null;
            Error = null;
        }
        catch (ToolException ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>Clearing the pad or dropping the image.</summary>
    public void ClearHandwriting()
    {
        Drawing = null;
        _imagePath = null;
        Image?.Dispose();
        Image = null;
    }
}
