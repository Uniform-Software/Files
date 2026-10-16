using System.Security.Cryptography.X509Certificates;

namespace UniFiles.Core.Signing;

/// <summary>Where certificates with their private keys live. The app uses the system's; tests use memory.</summary>
public interface ICertificateStore
{
    IReadOnlyList<X509Certificate2> Certificates();

    /// <summary>Adds a certificate and its private key.</summary>
    void Add(X509Certificate2 certificate);

    void Remove(X509Certificate2 certificate);

    /// <summary>How to load a .p12 before <see cref="Add"/>.</summary>
    X509KeyStorageFlags ImportFlags { get; }
}

/// <summary>
/// The user's personal certificates: the login keychain on macOS, the "Personal" store on Windows.
/// Smart cards and signing USB keys show there when their driver is installed; the system asks
/// for the PIN when the key is used. UniFiles never copies a private key anywhere else.
/// </summary>
public sealed class SystemCertificateStore : ICertificateStore
{
    /// <summary>
    /// Windows: the key must be persisted in the user's key storage, or it is deleted with the
    /// object that loaded it and the certificate in the store points at nothing ("key refused"
    /// when signing). macOS: never PersistKeySet, which writes straight into the login keychain on
    /// load; <see cref="Add"/> copies the key there itself.
    /// </summary>
    public X509KeyStorageFlags ImportFlags => OperatingSystem.IsWindows()
        ? X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet
        : X509KeyStorageFlags.DefaultKeySet;

    /// <summary>Empty when the store can't be read (no keychain, locked or missing store).</summary>
    public IReadOnlyList<X509Certificate2> Certificates()
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            return store.Certificates.Cast<X509Certificate2>().ToList();
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return [];
        }
    }

    public void Add(X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(certificate);
    }

    public void Remove(X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Remove(certificate);
    }
}

public sealed class MemoryCertificateStore : ICertificateStore
{
    private readonly List<X509Certificate2> _certificates = [];

    /// <summary>Kept in memory: nothing written to the keychain or the Windows key containers.</summary>
    public X509KeyStorageFlags ImportFlags => X509KeyStorageFlags.Exportable;

    public IReadOnlyList<X509Certificate2> Certificates() => _certificates.ToList();

    /// <summary>Like a keychain: adding a certificate again (now with its key) replaces it.</summary>
    public void Add(X509Certificate2 certificate)
    {
        _certificates.RemoveAll(c => c.Thumbprint == certificate.Thumbprint);
        _certificates.Add(certificate);
    }

    public void Remove(X509Certificate2 certificate) => _certificates.RemoveAll(c => c.Thumbprint == certificate.Thumbprint);
}
