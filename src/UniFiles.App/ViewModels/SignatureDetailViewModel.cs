using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniFiles.App.Services;
using UniFiles.Core.Resources;
using UniFiles.Core.Signing;
using UniFiles.Core.Tools;

namespace UniFiles.App.ViewModels;

/// <summary>One saved signature: its certificate, its handwritten signature (draw again, import, clear), export, removal.</summary>
public sealed partial class SignatureDetailViewModel : PageViewModel
{
    private readonly SignatureSettingsViewModel _owner;

    public SignatureDetailViewModel(SignatureSettingsViewModel owner, SigningIdentity identity, string? notice = null)
    {
        _owner = owner;
        Identity = identity;
        Item = new SignatureItemViewModel(identity);
        _notice = notice;
        _drawing = Item.Drawing;
        _image = Item.Image;
    }

    public SigningIdentity Identity { get; }

    public SignatureItemViewModel Item { get; }

    public bool CanRemove => Identity.CreatedByUniFiles;

    [ObservableProperty]
    private string? _notice;

    [ObservableProperty]
    private bool _noticeIsError;

    [ObservableProperty]
    private Handwriting? _drawing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private Bitmap? _image;

    public bool HasImage => Image is not null;

    /// <summary>Called by the pad when a stroke is finished.</summary>
    public void SaveDrawing(Handwriting drawing) => SigningContext.Handwriting.SaveDrawing(Identity.Id, drawing);

    [RelayCommand]
    private async Task ImportHandwriting()
    {
        if (await DialogService.PickFileAsync(Strings.SignSettings_HandwritingImport, Strings.SignSettings_ImageType, ["*.png", "*.jpg", "*.jpeg", "*.heic", "*.webp"]) is not { } path)
            return;
        try
        {
            SigningContext.Handwriting.ImportImage(Identity.Id, path);
            Image?.Dispose();
            Image = new Bitmap(SigningContext.Handwriting.ImagePath(Identity.Id)!);
            Drawing = null;
            Notice = null;
        }
        catch (ToolException ex)
        {
            Notice = ex.Message;
            NoticeIsError = true;
        }
    }

    [RelayCommand]
    private void ClearHandwriting()
    {
        SigningContext.Handwriting.Clear(Identity.Id);
        Image?.Dispose();
        Image = null;
        Drawing = null;
    }

    [RelayCommand]
    private async Task Export()
    {
        if (await DialogService.SaveFileAsync(Strings.SignSettings_Export, $"{Identity.Name}.cer", Strings.SignSettings_CerType, "cer") is { } path)
        {
            await File.WriteAllBytesAsync(path, IdentityStore.ExportPublic(Identity.Certificate));
            Notice = Strings.SignSettings_Exported;
            NoticeIsError = false;
        }
    }

    [RelayCommand]
    private void Remove()
    {
        SigningContext.Identities.Remove(Identity);
        _owner.Show();
    }

    [RelayCommand]
    private void Back() => _owner.Show();
}
