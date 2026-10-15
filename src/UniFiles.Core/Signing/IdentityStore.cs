using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using UniFiles.Core.Resources;
using UniFiles.Core.Tools;

namespace UniFiles.Core.Signing;

/// <summary>A certificate the user can sign with: the certificate and access to its private key.</summary>
public sealed record SigningIdentity(X509Certificate2 Certificate, bool CreatedByUniFiles, bool IsHardware)
{
    /// <summary>SHA-256 of the certificate, in hex: how settings and presets refer to it.</summary>
    public string Id { get; } = IdentityStore.IdOf(Certificate);

    public string Name => PdfSigner.SignerName(Certificate);

    public string? Email => Certificate.GetNameInfo(X509NameType.EmailName, forIssuer: false) is { Length: > 0 } email ? email : null;

    public string Issuer => Certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true);

    public bool IsSelfSigned => Certificate.SubjectName.RawData.AsSpan().SequenceEqual(Certificate.IssuerName.RawData);

    public DateTime NotAfter => Certificate.NotAfter;

    public bool IsValidNow => DateTime.Now >= Certificate.NotBefore && DateTime.Now <= Certificate.NotAfter;

    public bool ExpiresSoon => IsValidNow && Certificate.NotAfter - DateTime.Now < TimeSpan.FromDays(30);

    public bool DeclaredQualified => PdfSignatureVerifier.IsDeclaredQualified(Certificate);
}

/// <summary>An organization authority made by UniFiles, and the certificates it issued.</summary>
public sealed record OrganizationAuthority(X509Certificate2 Certificate, IReadOnlyList<IssuedCertificate> Issued)
{
    public string Id { get; } = IdentityStore.IdOf(Certificate);

    public string Name => Certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
}

public sealed record IssuedCertificate(string Name, string? Email, string SerialNumber, DateTime NotBefore, DateTime NotAfter);

/// <summary>
/// The signing identities: certificates of the system store that can sign (with a private key, an
/// allowed key usage), plus what UniFiles made or imported, the organization authorities it
/// created and the authorities the user chose to trust. Bookkeeping (which ones UniFiles made,
/// certificates issued, trusted authorities) lives in a folder of the app's data; private keys
/// stay in the store.
/// </summary>
public sealed class IdentityStore(ICertificateStore store, string folder)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string IdOf(X509Certificate2 certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);

    // ---- Identities ---------------------------------------------------------------------------------

    /// <summary>Certificates that can sign, valid ones first, then by name. Expired ones are listed (to be renewed or removed).</summary>
    public IReadOnlyList<SigningIdentity> Identities()
    {
        var state = Load();
        return store.Certificates()
            .Where(c => c.HasPrivateKey && CanSign(c))
            .Select(c => new SigningIdentity(c, state.Created.Contains(IdOf(c)), IsHardware(c)))
            .OrderByDescending(i => i.IsValidNow)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public SigningIdentity? Find(string? id) => id is null ? null : Identities().FirstOrDefault(i => i.Id == id);

    public SigningIdentity CreatePersonal(string name, string? email)
    {
        var certificate = CertificateFactory.CreatePersonal(name, email);
        store.Add(certificate);
        Update(state => state.Created.Add(IdOf(certificate)));
        return Find(IdOf(certificate))!;
    }

    /// <summary>Imports a .p12 / .pfx into the store; the password is used here and forgotten.</summary>
    public SigningIdentity Import(byte[] pkcs12, string password)
    {
        X509Certificate2Collection loaded;
        try
        {
            loaded = X509CertificateLoader.LoadPkcs12Collection(pkcs12, password, store.ImportFlags);
        }
        catch (CryptographicException ex)
        {
            throw new ToolException(Strings.Sign_WrongPassword, ex);
        }
        var certificate = loaded.Cast<X509Certificate2>().FirstOrDefault(c => c.HasPrivateKey)
            ?? throw new ToolException(Strings.Sign_NoPrivateKey);
        if (!CanSign(certificate))
            throw new ToolException(Strings.Sign_NotForSigning);
        store.Add(certificate);
        Update(state => state.Created.Add(IdOf(certificate)));
        return Find(IdOf(certificate)) ?? new SigningIdentity(certificate, true, false);
    }

    /// <summary>Removes a certificate UniFiles made or imported. Others are removed in the keychain or certificate manager.</summary>
    public void Remove(SigningIdentity identity)
    {
        if (!identity.CreatedByUniFiles)
            throw new InvalidOperationException("Only certificates made or imported by UniFiles are removed here.");
        store.Remove(identity.Certificate);
        Update(state => state.Created.Remove(identity.Id));
        new HandwritingStore(folder).Clear(identity.Id);
    }

    /// <summary>The certificate without its key (.cer), to give to the people who receive signed documents.</summary>
    public static byte[] ExportPublic(X509Certificate2 certificate) => certificate.Export(X509ContentType.Cert);

    /// <summary>Intermediate certificates of the identity found offline (system and store), to embed in signatures.</summary>
    public IReadOnlyList<X509Certificate2> ChainOf(SigningIdentity identity)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
        chain.ChainPolicy.ExtraStore.AddRange(store.Certificates().ToArray());
        chain.ChainPolicy.ExtraStore.AddRange(Authorities().Select(a => a.Certificate).ToArray());
        chain.Build(identity.Certificate);
        return chain.ChainElements
            .Select(e => e.Certificate)
            .Skip(1)
            .Where(c => !c.SubjectName.RawData.AsSpan().SequenceEqual(c.IssuerName.RawData))
            .ToList();
    }

    // ---- Organization authorities --------------------------------------------------------------------

    public IReadOnlyList<OrganizationAuthority> Authorities()
    {
        var state = Load();
        return store.Certificates()
            .Where(c => c.HasPrivateKey && state.Authorities.Contains(IdOf(c)))
            .Select(c => new OrganizationAuthority(c, state.Issued.Where(i => i.Authority == IdOf(c)).Select(i => i.Certificate).ToList()))
            .ToList();
    }

    public OrganizationAuthority CreateAuthority(string organization)
    {
        var certificate = CertificateFactory.CreateAuthority(organization);
        store.Add(certificate);
        Update(state => state.Authorities.Add(IdOf(certificate)));
        return Authorities().First(a => a.Id == IdOf(certificate));
    }

    /// <summary>Issues a member's certificate; returns it as a password-protected .p12 to hand over.</summary>
    public byte[] Issue(OrganizationAuthority authority, string name, string? email, string password)
    {
        using var issued = CertificateFactory.Issue(authority.Certificate, name, email);
        Update(state => state.Issued.Add(new IssuedRecord(authority.Id,
            new IssuedCertificate(name, email, issued.SerialNumber, issued.NotBefore, issued.NotAfter))));
        var collection = new X509Certificate2Collection { issued, X509CertificateLoader.LoadCertificate(authority.Certificate.RawData) };
        return collection.Export(X509ContentType.Pkcs12, password)!;
    }

    // ---- Trusted authorities (for checking signatures) ---------------------------------------------------

    /// <summary>Authorities whose members' signatures count as verified: the user's own, and those they accepted.</summary>
    public IReadOnlyList<X509Certificate2> TrustedAuthorities()
    {
        var trusted = new List<X509Certificate2>(Authorities().Select(a => X509CertificateLoader.LoadCertificate(a.Certificate.RawData)));
        var path = Path.Combine(folder, "trusted");
        if (Directory.Exists(path))
            foreach (var file in Directory.EnumerateFiles(path, "*.cer"))
                try
                {
                    trusted.Add(X509CertificateLoader.LoadCertificateFromFile(file));
                }
                catch (CryptographicException)
                {
                    // A damaged file is skipped, not fatal.
                }
        return trusted;
    }

    /// <summary>Accepts an authority (its .cer) for checking signatures. Only authorities are accepted.</summary>
    public X509Certificate2 Trust(byte[] certificateData)
    {
        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadCertificate(certificateData);
        }
        catch (CryptographicException ex)
        {
            throw new ToolException(Strings.Sign_NotACertificate, ex);
        }
        if (certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault() is not { CertificateAuthority: true })
            throw new ToolException(Strings.Sign_NotAnAuthority);
        var path = Path.Combine(folder, "trusted");
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, IdOf(certificate) + ".cer"), certificate.Export(X509ContentType.Cert));
        return certificate;
    }

    public void Untrust(X509Certificate2 certificate)
    {
        var file = Path.Combine(folder, "trusted", IdOf(certificate) + ".cer");
        if (File.Exists(file))
            File.Delete(file);
    }

    // ---- Helpers --------------------------------------------------------------------------------------

    /// <summary>Purposes that allow signing documents: document signing (RFC 9336, Adobe, Microsoft), email protection, any.</summary>
    private static readonly HashSet<string> DocumentPurposes =
    [
        "1.3.6.1.5.5.7.3.36", "1.2.840.113583.1.1.5", "1.3.6.1.4.1.311.10.3.12", "1.3.6.1.5.5.7.3.4", "2.5.29.37.0",
    ];

    /// <summary>
    /// Certificates for signing documents: not an authority; key usage digital signature or
    /// non-repudiation (or none); and, when purposes are listed, one for documents. Leaves out code
    /// signing (developer certificates), web servers and login certificates.
    /// </summary>
    public static bool CanSign(X509Certificate2 certificate)
    {
        if (certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault() is { CertificateAuthority: true })
            return false;
        if (certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault() is { } usage
            && (usage.KeyUsages & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) == 0)
            return false;
        return certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault() is not { } purposes
            || purposes.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(o => o.Value is { } id && DocumentPurposes.Contains(id));
    }

    /// <summary>Whether the key sits on a smart card or USB key (known under Windows; macOS doesn't say through .NET).</summary>
    private static bool IsHardware(X509Certificate2 certificate)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            using var rsa = certificate.GetRSAPrivateKey();
            if (rsa is RSACng cng)
                return cng.Key.Provider?.Provider.Contains("Smart Card", StringComparison.OrdinalIgnoreCase) == true;
            using var ec = certificate.GetECDsaPrivateKey();
            return ec is ECDsaCng ecCng && ecCng.Key.Provider?.Provider.Contains("Smart Card", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private sealed record IssuedRecord(string Authority, IssuedCertificate Certificate);

    private sealed class State
    {
        public HashSet<string> Created { get; set; } = [];

        public HashSet<string> Authorities { get; set; } = [];

        public List<IssuedRecord> Issued { get; set; } = [];
    }

    private string StateFile => Path.Combine(folder, "identities.json");

    private State Load()
    {
        try
        {
            if (File.Exists(StateFile))
                return JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile), Json) ?? new State();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Lost bookkeeping only hides the "made by UniFiles" badges; keys are in the store.
        }
        return new State();
    }

    private void Update(Action<State> change)
    {
        var state = Load();
        change(state);
        Directory.CreateDirectory(folder);
        File.WriteAllText(StateFile, JsonSerializer.Serialize(state, Json));
    }
}
