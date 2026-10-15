using UniFiles.Core.Storage;

namespace UniFiles.Core.Signing;

/// <summary>
/// Where signing finds its identities and handwritten signature: the system store and a folder of
/// the app's data by default; tests replace them with a memory store and a temporary folder.
/// </summary>
public static class SigningContext
{
    private static IdentityStore? _identities;
    private static HandwritingStore? _handwriting;

    public static string Folder => Path.Combine(SettingsStore.AppDataDirectory, "signature");

    public static IdentityStore Identities
    {
        get => _identities ??= new IdentityStore(new SystemCertificateStore(), Folder);
        set => _identities = value;
    }

    public static HandwritingStore Handwriting
    {
        get => _handwriting ??= new HandwritingStore(Folder);
        set => _handwriting = value;
    }
}
