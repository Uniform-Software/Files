using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using UniFiles.Core.Resources;
using UniFiles.Core.Tools;
using UniFiles.Core.Tools.Pdf;

namespace UniFiles.Core.Signing;

/// <summary>What happened to the document after a signature.</summary>
public enum SignatureCoverage
{
    /// <summary>The signature covers the whole file.</summary>
    WholeDocument,

    /// <summary>Later revisions only add signatures (another signer): allowed.</summary>
    ExtendedWithSignatures,

    /// <summary>Later revisions change the document (content, pages, other fields).</summary>
    ModifiedAfter,
}

public enum CertificateStatus
{
    /// <summary>Chains to an authority trusted by the system or accepted by the user.</summary>
    Trusted,

    /// <summary>Self-signed or from an unknown authority: the identity is not vouched for.</summary>
    UnknownIdentity,

    /// <summary>Not valid at the signing time (expired, not yet valid).</summary>
    NotValidAtSigningTime,
}

public enum SignatureVerdict
{
    Valid,
    ValidUnverifiedIdentity,
    ModifiedAfterSigning,
    Invalid,
}

public sealed record SignatureReport
{
    public required string FieldName { get; init; }

    public required SignatureVerdict Verdict { get; init; }

    /// <summary>The signed bytes match the signature (false: tampered or broken).</summary>
    public required bool Intact { get; init; }

    public required SignatureCoverage Coverage { get; init; }

    public X509Certificate2? Certificate { get; init; }

    public CertificateStatus CertificateStatus { get; init; }

    public string SignerName { get; init; } = "";

    public string? Email { get; init; }

    public string? Issuer { get; init; }

    /// <summary>Time written by the signer's computer (/M), not proven.</summary>
    public DateTimeOffset? ClaimedTime { get; init; }

    /// <summary>Time proven by a time-stamping authority, when there is a valid token.</summary>
    public DateTimeOffset? TimestampTime { get; init; }

    /// <summary>The certificate says it is qualified (eIDAS) and its key is on a qualified device.</summary>
    public bool DeclaredQualified { get; init; }

    /// <summary>"PAdES B-T", "PAdES B-B", "adbe.pkcs7.detached"…</summary>
    public string Format { get; init; } = "";

    /// <summary>signing-certificate-v2 present and matching (PAdES); false for older signatures.</summary>
    public bool CertificateBound { get; init; }

    public string? Reason { get; init; }

    public string? Location { get; init; }
}

/// <summary>
/// Checks the signatures of a PDF, offline (after ETSI EN 319 102-1, simplified): integrity of the
/// signed bytes, what changed afterwards, the certificate at the signing time, time-stamp, format.
/// Revocation is not checked (no network), and "qualified" is what the certificate declares.
/// </summary>
public static class PdfSignatureVerifier
{
    private const string QcStatements = "1.3.6.1.5.5.7.1.3";
    private const string QcCompliance = "0.4.0.1862.1.1";
    private const string QcSscd = "0.4.0.1862.1.4";

    public static IReadOnlyList<SignatureReport> Verify(byte[] pdf, IEnumerable<X509Certificate2>? trustedAuthorities = null)
    {
        PdfDocument document;
        try
        {
            document = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ToolException(Strings.Error_PdfUnreadable, ex);
        }

        using (document)
        {
            var authorities = trustedAuthorities?.ToList() ?? [];
            var reports = new List<SignatureReport>();
            foreach (var (name, signature) in SignatureFields(document))
                reports.Add(Check(pdf, document, name, signature, authorities));
            return reports;
        }
    }

    private static SignatureReport Check(byte[] pdf, PdfDocument document, string name, PdfDictionary signature, List<X509Certificate2> authorities)
    {
        var subFilter = signature.Elements.GetName("/SubFilter").TrimStart('/');
        var claimed = ParseDate(signature.Elements.GetString("/M"));
        var range = PdfForms.ResolveArray(signature.Elements["/ByteRange"])?.Elements.Select(PdfForms.Number).Select(n => (long)n).ToArray();
        var broken = new SignatureReport
        {
            FieldName = name, Verdict = SignatureVerdict.Invalid, Intact = false, Coverage = SignatureCoverage.ModifiedAfter,
            Format = subFilter, ClaimedTime = claimed,
        };
        if (range is not { Length: 4 } || range[0] != 0 || range[1] < 0 || range[2] < range[1] || range[2] + range[3] > pdf.Length)
            return broken;

        // Contents as written in the file (hex between the two ranges), cut to its DER length.
        SignedCms cms;
        byte[] signed;
        try
        {
            var hex = Encoding.ASCII.GetString(pdf, (int)range[1] + 1, (int)(range[2] - range[1]) - 2);
            var der = Convert.FromHexString(hex);
            AsnDecoder.ReadEncodedValue(der, AsnEncodingRules.BER, out _, out _, out var length);
            signed = new byte[range[1] + range[3]];
            Array.Copy(pdf, 0, signed, 0, range[1]);
            Array.Copy(pdf, range[2], signed, range[1], range[3]);
            cms = new SignedCms(new ContentInfo(signed), detached: true);
            cms.Decode(der.AsSpan(0, length));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or AsnContentException or ArgumentException)
        {
            return broken;
        }

        var signer = cms.SignerInfos[0];
        var certificate = signer.Certificate;
        bool intact;
        try
        {
            cms.CheckSignature(verifySignatureOnly: true);
            intact = certificate is not null;
        }
        catch (CryptographicException)
        {
            intact = false;
        }

        DateTimeOffset? timestamp = null;
        foreach (CryptographicAttributeObject attribute in signer.UnsignedAttributes)
            if (attribute.Oid.Value == CadesSigner.SignatureTimeStampToken)
                foreach (var value in attribute.Values)
                    if (Rfc3161TimestampToken.TryDecode(value.RawData, out var token, out _) && token.VerifySignatureForSignerInfo(signer, out _))
                        timestamp = token.TokenInfo.Timestamp;

        var bound = SigningCertificateBound(signer, certificate);
        var coverage = range[2] + range[3] >= TrimmedLength(pdf)
            ? SignatureCoverage.WholeDocument
            : LaterChangesAreSignatures(pdf, range[2] + range[3], document) ? SignatureCoverage.ExtendedWithSignatures : SignatureCoverage.ModifiedAfter;
        var status = certificate is null
            ? CertificateStatus.UnknownIdentity
            : CertificateAt(certificate, cms.Certificates, (timestamp ?? claimed ?? DateTimeOffset.Now).UtcDateTime, authorities);

        var verdict = !intact ? SignatureVerdict.Invalid
            : coverage == SignatureCoverage.ModifiedAfter ? SignatureVerdict.ModifiedAfterSigning
            : status == CertificateStatus.Trusted ? SignatureVerdict.Valid
            : SignatureVerdict.ValidUnverifiedIdentity;

        return new SignatureReport
        {
            FieldName = name,
            Verdict = verdict,
            Intact = intact,
            Coverage = coverage,
            Certificate = certificate,
            CertificateStatus = status,
            SignerName = certificate is null ? signature.Elements.GetString("/Name") : PdfSigner.SignerName(certificate),
            Email = certificate?.GetNameInfo(X509NameType.EmailName, forIssuer: false) is { Length: > 0 } email ? email : null,
            Issuer = certificate?.GetNameInfo(X509NameType.SimpleName, forIssuer: true),
            ClaimedTime = claimed,
            TimestampTime = timestamp,
            DeclaredQualified = certificate is not null && IsDeclaredQualified(certificate),
            Format = subFilter == "ETSI.CAdES.detached" ? (timestamp is null ? "PAdES B-B" : "PAdES B-T") : subFilter,
            CertificateBound = bound,
            Reason = NullIfEmpty(signature.Elements.GetString("/Reason")),
            Location = NullIfEmpty(signature.Elements.GetString("/Location")),
        };
    }

    // ---- Fields ------------------------------------------------------------------------------------

    private static IEnumerable<(string Name, PdfDictionary Signature)> SignatureFields(PdfDocument document)
    {
        if (PdfForms.Resolve(document.Internals.Catalog.Elements["/AcroForm"]) is not { } form)
            yield break;
        var stack = new Stack<(PdfItem Item, string Prefix, string? Type)>();
        foreach (var item in (PdfForms.ResolveArray(form.Elements["/Fields"])?.Elements.ToList() ?? []).AsEnumerable().Reverse())
            stack.Push((item, "", null));
        var seen = new HashSet<PdfDictionary>();
        while (stack.Count > 0)
        {
            var (item, prefix, inherited) = stack.Pop();
            if (PdfForms.Resolve(item) is not { } field || !seen.Add(field))
                continue;
            var partial = field.Elements.GetString("/T");
            var full = string.IsNullOrEmpty(partial) ? prefix : prefix.Length == 0 ? partial : prefix + "." + partial;
            var type = field.Elements.GetName("/FT") is { Length: > 0 } t ? t : inherited;
            if (type == "/Sig" && PdfForms.Resolve(field.Elements["/V"]) is { } value)
                yield return (full, value);
            foreach (var kid in (PdfForms.ResolveArray(field.Elements["/Kids"])?.Elements.ToList() ?? []).AsEnumerable().Reverse())
                stack.Push((kid, full, type));
        }
    }

    // ---- Changes after a signature ---------------------------------------------------------------------

    /// <summary>
    /// Compares the signed revision with the whole file, object by object: changes are allowed only
    /// where another signature attaches (form field list, page annotations, an empty signature field
    /// being filled, signature flags and resources).
    /// </summary>
    private static bool LaterChangesAreSignatures(byte[] pdf, long end, PdfDocument latest)
    {
        PdfDocument revision;
        try
        {
            revision = PdfReader.Open(new MemoryStream(pdf, 0, (int)end), PdfDocumentOpenMode.Import);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }

        using (revision)
        {
            if (revision.PageCount != latest.PageCount)
                return false;
            foreach (var before in revision.Internals.GetAllObjects())
            {
                var after = latest.Internals.GetObject(before.Reference!.ObjectID);
                if (after is null)
                    return false;
                if (Serialize(revision, before).AsSpan().SequenceEqual(Serialize(latest, after)))
                    continue;
                if (!AllowedChange(revision, before, latest, after))
                    return false;
            }
            return true;
        }
    }

    private static bool AllowedChange(PdfDocument revision, PdfObject before, PdfDocument latest, PdfObject after)
    {
        switch (before, after)
        {
            case (PdfArray a, PdfArray b):
                return IsExtendedWithSignatures(revision, a, latest, b);
            case (PdfDictionary a, PdfDictionary b):
                var type = a.Elements.GetName("/Type");
                if (type == "/Catalog")
                    return SameExcept(revision, a, latest, b, "/AcroForm", "/DSS", "/Perms")
                        && AllowedAcroForm(revision, a.Elements["/AcroForm"], latest, b.Elements["/AcroForm"]);
                if (type == "/Page")
                    return SameExcept(revision, a, latest, b, "/Annots")
                        && ExtendedArray(revision, a.Elements["/Annots"], latest, b.Elements["/Annots"]);
                if (a.Elements.GetName("/FT") == "/Sig" && a.Elements["/V"] is null)
                    return SameExcept(revision, a, latest, b, "/V", "/AP", "/AS");
                if (a.Elements.ContainsKey("/Fields"))
                    return AllowedAcroForm(revision, a, latest, b);
                return false;
            default:
                return false;
        }
    }

    private static bool AllowedAcroForm(PdfDocument revision, PdfItem? before, PdfDocument latest, PdfItem? after)
    {
        if (before is PdfReference || after is PdfReference || (before is null && after is null))
            return true; // an indirect form is compared on its own
        if (PdfForms.Resolve(after) is not { } b)
            return false;
        if (PdfForms.Resolve(before) is not { } a)
            return true; // the form was created for the signature
        return SameExcept(revision, a, latest, b, "/Fields", "/SigFlags", "/DR", "/DA")
            && ExtendedArray(revision, a.Elements["/Fields"], latest, b.Elements["/Fields"]);
    }

    private static bool ExtendedArray(PdfDocument revision, PdfItem? before, PdfDocument latest, PdfItem? after)
    {
        if (before is PdfReference && after is PdfReference)
            return true; // compared as an object of its own
        var b = PdfForms.ResolveArray(after);
        var a = PdfForms.ResolveArray(before);
        return b is not null && IsExtendedWithSignatures(revision, a, latest, b);
    }

    /// <summary>The same items, followed only by signature fields.</summary>
    private static bool IsExtendedWithSignatures(PdfDocument revision, PdfArray? before, PdfDocument latest, PdfArray after)
    {
        var count = before?.Elements.Count ?? 0;
        if (after.Elements.Count < count)
            return false;
        for (var i = 0; i < count; i++)
            if (!Serialize(revision, before!.Elements[i]).AsSpan().SequenceEqual(Serialize(latest, after.Elements[i])))
                return false;
        for (var i = count; i < after.Elements.Count; i++)
            if (PdfForms.Resolve(after.Elements[i]) is not { } added || !IsSignatureField(added))
                return false;
        return true;
    }

    private static bool IsSignatureField(PdfDictionary item) =>
        item.Elements.GetName("/FT") == "/Sig" || (PdfForms.Resolve(item.Elements["/Parent"]) is { } parent && parent.Elements.GetName("/FT") == "/Sig");

    private static bool SameExcept(PdfDocument revision, PdfDictionary a, PdfDocument latest, PdfDictionary b, params string[] keys)
    {
        var names = a.Elements.Keys.Union(b.Elements.Keys).Except(keys);
        foreach (var key in names)
        {
            var x = a.Elements[key];
            var y = b.Elements[key];
            if (x is null || y is null)
                return false;
            if (!Serialize(revision, x).AsSpan().SequenceEqual(Serialize(latest, y)))
                return false;
        }
        return true;
    }

    private static byte[] Serialize(PdfDocument document, PdfItem item)
    {
        using var buffer = new MemoryStream();
        document.Internals.WriteObject(buffer, item);
        return buffer.ToArray();
    }

    /// <summary>Length without trailing whitespace (some tools add a line break after %%EOF).</summary>
    private static long TrimmedLength(byte[] pdf)
    {
        var length = pdf.Length;
        while (length > 0 && pdf[length - 1] is (byte)'\n' or (byte)'\r' or (byte)' ' or 0)
            length--;
        return length;
    }

    // ---- Certificate --------------------------------------------------------------------------------

    private static CertificateStatus CertificateAt(X509Certificate2 certificate, X509Certificate2Collection embedded, DateTime when, List<X509Certificate2> authorities)
    {
        if (when < certificate.NotBefore.ToUniversalTime() || when > certificate.NotAfter.ToUniversalTime())
            return CertificateStatus.NotValidAtSigningTime;

        // System trust first, then the organization authorities the user accepted. Never online.
        foreach (var custom in authorities.Count == 0 ? [false] : new[] { false, true })
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.VerificationTime = when;
            chain.ChainPolicy.ExtraStore.AddRange(embedded);
            if (custom)
            {
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.AddRange(authorities.ToArray());
            }
            if (chain.Build(certificate))
                return CertificateStatus.Trusted;
        }
        return CertificateStatus.UnknownIdentity;
    }

    private static bool SigningCertificateBound(SignerInfo signer, X509Certificate2? certificate)
    {
        if (certificate is null)
            return false;
        foreach (CryptographicAttributeObject attribute in signer.SignedAttributes)
            if (attribute.Oid.Value == CadesSigner.SigningCertificateV2)
                foreach (var value in attribute.Values)
                    try
                    {
                        // SEQUENCE { SEQUENCE OF ESSCertIDv2 { [AlgorithmIdentifier] OCTET STRING ... } }
                        var outer = new AsnReader(value.RawData, AsnEncodingRules.DER).ReadSequence();
                        var first = outer.ReadSequence().ReadSequence();
                        var algorithm = Sha256Name;
                        if (first.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
                            algorithm = first.ReadSequence().ReadObjectIdentifier();
                        var hash = first.ReadOctetString();
                        var expected = algorithm switch
                        {
                            Sha256Name => SHA256.HashData(certificate.RawData),
                            "2.16.840.1.101.3.4.2.2" => SHA384.HashData(certificate.RawData),
                            "2.16.840.1.101.3.4.2.3" => SHA512.HashData(certificate.RawData),
                            _ => null,
                        };
                        return expected is not null && expected.AsSpan().SequenceEqual(hash);
                    }
                    catch (AsnContentException)
                    {
                        return false;
                    }
        return false;
    }

    private const string Sha256Name = CadesSigner.Sha256;

    /// <summary>QcStatements (ETSI EN 319 412-5) with QcCompliance and QcSSCD: qualified, key on a qualified device.</summary>
    public static bool IsDeclaredQualified(X509Certificate2 certificate)
    {
        if (certificate.Extensions[QcStatements] is not { } extension)
            return false;
        try
        {
            var statements = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
            bool compliance = false, sscd = false;
            while (statements.HasData)
            {
                var statement = statements.ReadSequence();
                var id = statement.ReadObjectIdentifier();
                compliance |= id == QcCompliance;
                sscd |= id == QcSscd;
            }
            return compliance && sscd;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    /// <summary>"D:20261015143200+02'00'" and its shorter forms.</summary>
    internal static DateTimeOffset? ParseDate(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        var s = text.StartsWith("D:", StringComparison.Ordinal) ? text[2..] : text;
        var digits = new string(s.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length < 4)
            return null;
        int Part(int start, int length, int fallback) => digits.Length >= start + length ? int.Parse(digits.AsSpan(start, length)) : fallback;
        var offset = TimeSpan.Zero;
        var rest = s[digits.Length..];
        if (rest.Length > 0 && rest[0] is '+' or '-')
        {
            var numbers = new string(rest[1..].Where(char.IsDigit).ToArray());
            var hours = numbers.Length >= 2 ? int.Parse(numbers.AsSpan(0, 2)) : 0;
            var minutes = numbers.Length >= 4 ? int.Parse(numbers.AsSpan(2, 2)) : 0;
            offset = new TimeSpan(hours, minutes, 0) * (rest[0] == '-' ? -1 : 1);
        }
        try
        {
            return new DateTimeOffset(Part(0, 4, 1), Part(4, 2, 1), Part(6, 2, 1), Part(8, 2, 0), Part(10, 2, 0), Part(12, 2, 0), offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
