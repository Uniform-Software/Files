using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace UniFiles.Core.Signing;

/// <summary>Asks a time-stamping authority for a token over a SHA-256 digest (RFC 3161); returns the encoded token.</summary>
public delegate Task<byte[]> TimestampProvider(byte[] sha256Digest, CancellationToken cancellationToken);

/// <summary>
/// The cryptographic part of a PAdES signature (ETSI EN 319 142-1, baseline B-B / B-T): a detached
/// CMS SignedData over the signed bytes, SHA-256, with the signed attributes PAdES expects
/// (content-type, message-digest, signing-certificate-v2) and no signing-time (the PDF's /M holds it).
/// The private key is used through the system (keychain, certificate store, smart card): it never
/// leaves where it is kept.
/// </summary>
public static class CadesSigner
{
    public const string SigningCertificateV2 = "1.2.840.113549.1.9.16.2.47";
    public const string SignatureTimeStampToken = "1.2.840.113549.1.9.16.2.14";
    public const string Sha256 = "2.16.840.1.101.3.4.2.1";

    public static async Task<byte[]> SignAsync(byte[] content, X509Certificate2 certificate, IEnumerable<X509Certificate2>? chain,
        TimestampProvider? timestamp, CancellationToken cancellationToken)
    {
        // The chain is given, never fetched: building it could go online (authority information access).
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate)
        {
            DigestAlgorithm = new Oid(Sha256),
            IncludeOption = X509IncludeOption.EndCertOnly,
        };
        foreach (var extra in chain ?? [])
            if (extra.Thumbprint != certificate.Thumbprint && extra.SubjectName.RawData.AsSpan().SequenceEqual(extra.IssuerName.RawData) is false)
                signer.Certificates.Add(extra);
        signer.SignedAttributes.Add(new AsnEncodedData(SigningCertificateV2, EncodeSigningCertificateV2(certificate)));

        var cms = new SignedCms(new ContentInfo(content), detached: true);
        cms.ComputeSignature(signer, silent: false);

        if (timestamp is not null)
        {
            var signerInfo = cms.SignerInfos[0];
            var token = await timestamp(SHA256.HashData(signerInfo.GetSignature()), cancellationToken);
            signerInfo.AddUnsignedAttribute(new AsnEncodedData(SignatureTimeStampToken, token));
        }
        return cms.Encode();
    }

    /// <summary>
    /// SigningCertificateV2 ::= SEQUENCE { certs SEQUENCE OF ESSCertIDv2 }, ESSCertIDv2 ::= SEQUENCE
    /// { hashAlgorithm DEFAULT sha256, certHash OCTET STRING, issuerSerial OPTIONAL } (RFC 5035).
    /// </summary>
    internal static byte[] EncodeSigningCertificateV2(X509Certificate2 certificate)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence())
        using (writer.PushSequence())
            writer.WriteOctetString(SHA256.HashData(certificate.RawData));
        return writer.Encode();
    }
}
