using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace UniFiles.Core.Tests;

/// <summary>Certificates made in memory (never in the keychain) and a raw check of the signatures of a PDF.</summary>
internal static partial class SigningTestKit
{
    public static X509Certificate2 Certificate(string name = "Élodie Martin", bool ecdsa = false)
    {
        CertificateRequest request;
        if (ecdsa)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
            return Finish(request);
        }
        using var rsa = RSA.Create(3072);
        request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Finish(request);

        static X509Certificate2 Finish(CertificateRequest request)
        {
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
            using var made = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
            return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx, ""), "", X509KeyStorageFlags.Exportable);
        }
    }

    /// <summary>An organization authority and a certificate it issued to a member.</summary>
    public static (X509Certificate2 Authority, X509Certificate2 Member) AuthorityAndMember(string member = "Zoé Bernard")
    {
        using var caKey = RSA.Create(3072);
        var caRequest = new CertificateRequest("CN=Association des Lilas, O=Association des Lilas", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, false));
        using var authority = caRequest.CreateSelfSigned(DateTimeOffset.Now.AddDays(-2), DateTimeOffset.Now.AddYears(10));

        using var memberKey = RSA.Create(3072);
        var request = new CertificateRequest($"CN={member}", memberKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, false));
        var serial = RandomNumberGenerator.GetBytes(12);
        using var issued = request.Create(authority, DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(3), serial);
        using var withKey = issued.CopyWithPrivateKey(memberKey);
        return (X509CertificateLoader.LoadCertificate(authority.RawData),
            X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx, ""), "", X509KeyStorageFlags.Exportable));
    }

    /// <summary>A self-signed certificate valid between two dates (for expired-certificate tests).</summary>
    public static X509Certificate2 CertificateValid(DateTimeOffset from, DateTimeOffset to, X509Extension? extra = null)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Ancien Certificat", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (extra is not null)
            request.CertificateExtensions.Add(extra);
        using var made = request.CreateSelfSigned(from, to);
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx, ""), "", X509KeyStorageFlags.Exportable);
    }

    public sealed record RawSignature(long[] ByteRange, bool CoversWholeFile, bool Valid, SignedCms? Cms, string SubFilter);

    /// <summary>Every signature of the file, checked against its byte range (no PDF library involved).</summary>
    public static List<RawSignature> Signatures(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var result = new List<RawSignature>();
        foreach (Match match in ByteRangePattern().Matches(text))
        {
            var range = Enumerable.Range(1, 4).Select(i => long.Parse(match.Groups[i].Value)).ToArray();
            var hexStart = (int)(range[0] + range[1]) + 1;
            var hexEnd = (int)range[2] - 1;
            var subFilter = SubFilterPattern().Matches(text[Math.Max(0, (int)range[1] - 4000)..(int)range[1]]) is { Count: > 0 } found ? found[^1].Groups[1].Value : "";
            SignedCms? cms = null;
            var valid = false;
            try
            {
                var hex = text[hexStart..hexEnd];
                var der = Convert.FromHexString(hex);
                var length = System.Formats.Asn1.AsnDecoder.ReadEncodedValue(der, System.Formats.Asn1.AsnEncodingRules.BER, out _, out _, out var consumed);
                var signed = pdf[(int)range[0]..(int)(range[0] + range[1])].Concat(pdf[(int)range[2]..(int)(range[2] + range[3])]).ToArray();
                cms = new SignedCms(new ContentInfo(signed), detached: true);
                cms.Decode(der[..consumed]);
                cms.CheckSignature(verifySignatureOnly: true);
                valid = true;
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException or System.Formats.Asn1.AsnContentException)
            {
            }
            result.Add(new RawSignature(range, range[2] + range[3] == pdf.Length, valid, cms, subFilter));
        }
        return result;
    }

    /// <summary>A minimal PDF 1.5 whose cross-references are a stream (like Word's and Acrobat's), with one page.</summary>
    public static byte[] XrefStreamPdf()
    {
        var output = new MemoryStream();
        var offsets = new List<long>();
        void Write(string s) => output.Write(Encoding.Latin1.GetBytes(s));
        Write("%PDF-1.5\n%âãÏÓ\n");
        offsets.Add(output.Position); Write("1 0 obj\n<</Type/Catalog/Pages 2 0 R>>\nendobj\n");
        offsets.Add(output.Position); Write("2 0 obj\n<</Type/Pages/Kids[3 0 R]/Count 1>>\nendobj\n");
        offsets.Add(output.Position); Write("3 0 obj\n<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R>>\nendobj\n");
        var content = "BT /F1 1 Tf ET";
        offsets.Add(output.Position); Write($"4 0 obj\n<</Length {content.Length}>>\nstream\n{content}\nendstream\nendobj\n");
        var xref = output.Position;
        var entries = new MemoryStream();
        entries.Write([0, 0, 0, 0, 0, 0xFF, 0xFF]);
        foreach (var offset in offsets.Append(xref))
            entries.Write([1, (byte)(offset >> 24), (byte)(offset >> 16), (byte)(offset >> 8), (byte)offset, 0, 0]);
        Write($"5 0 obj\n<</Type/XRef/Size 6/W[1 4 2]/Root 1 0 R/ID[<0123456789ABCDEF0123456789ABCDEF><0123456789ABCDEF0123456789ABCDEF>]/Length {entries.Length}>>\nstream\n");
        output.Write(entries.ToArray());
        Write($"\nendstream\nendobj\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    [GeneratedRegex(@"/ByteRange\s*\[\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*\]")]
    private static partial Regex ByteRangePattern();

    [GeneratedRegex(@"/SubFilter\s*/([A-Za-z0-9.]+)")]
    private static partial Regex SubFilterPattern();
}
