using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace UniFiles.Core.Signing;

/// <summary>Certificates UniFiles makes: a personal one, an organization authority, and certificates the authority issues.</summary>
public static class CertificateFactory
{
    private const string DocumentSigning = "1.3.6.1.5.5.7.3.36"; // id-kp-documentSigning (RFC 9336)
    private const string EmailProtection = "1.3.6.1.5.5.7.3.4";

    /// <summary>Self-signed, RSA 3072, SHA-256, 3 years, for signing documents.</summary>
    public static X509Certificate2 CreatePersonal(string name, string? email, DateTimeOffset? now = null)
    {
        var start = (now ?? DateTimeOffset.Now).AddMinutes(-5);
        using var key = RSA.Create(3072);
        var request = SigningRequest(name, email, key);
        using var certificate = request.CreateSelfSigned(start, start.AddYears(3));
        return Exportable(certificate);
    }

    /// <summary>An authority for an organization: RSA 4096, 10 years, may only issue certificates.</summary>
    public static X509Certificate2 CreateAuthority(string organization, DateTimeOffset? now = null)
    {
        var start = (now ?? DateTimeOffset.Now).AddMinutes(-5);
        using var key = RSA.Create(4096);
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName(organization);
        subject.AddOrganizationName(organization);
        var request = new CertificateRequest(subject.Build(), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var certificate = request.CreateSelfSigned(start, start.AddYears(10));
        return Exportable(certificate);
    }

    /// <summary>A member's certificate (3 years) signed by the authority, with its key, ready to export as .p12.</summary>
    public static X509Certificate2 Issue(X509Certificate2 authority, string name, string? email, DateTimeOffset? now = null)
    {
        if (!authority.HasPrivateKey)
            throw new ArgumentException("The authority's private key is needed to issue certificates.", nameof(authority));
        var start = (now ?? DateTimeOffset.Now).AddMinutes(-5);
        var end = start.AddYears(3);
        if (end > authority.NotAfter)
            end = authority.NotAfter;
        using var key = RSA.Create(3072);
        var request = SigningRequest(name, email, key);
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, false));
        using var issued = request.Create(authority, start, end, RandomNumberGenerator.GetBytes(16));
        using var withKey = issued.CopyWithPrivateKey(key);
        return Exportable(withKey);
    }

    private static CertificateRequest SigningRequest(string name, string? email, RSA key)
    {
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName(name);
        if (!string.IsNullOrWhiteSpace(email))
            subject.AddEmailAddress(email);
        var request = new CertificateRequest(subject.Build(), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(DocumentSigning), new Oid(EmailProtection)], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        if (!string.IsNullOrWhiteSpace(email))
        {
            var alternative = new SubjectAlternativeNameBuilder();
            alternative.AddEmailAddress(email);
            request.CertificateExtensions.Add(alternative.Build());
        }
        return request;
    }

    /// <summary>An ephemeral key can't go to a keychain: round-trip through PKCS#12 so the store can take it.</summary>
    private static X509Certificate2 Exportable(X509Certificate2 certificate) =>
        X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12, ""), "", X509KeyStorageFlags.Exportable);
}
