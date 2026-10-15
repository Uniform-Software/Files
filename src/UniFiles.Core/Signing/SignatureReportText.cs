using System.Globalization;
using System.Text;
using UniFiles.Core.Resources;

namespace UniFiles.Core.Signing;

/// <summary>A signature check, written for people: a heading per signature, the verdict first.</summary>
public static class SignatureReportText
{
    public static string Markdown(string fileName, IReadOnlyList<SignatureReport> reports)
    {
        var culture = CultureInfo.CurrentCulture;
        var text = new StringBuilder();
        text.Append(culture, $"# {fileName}\n\n");
        if (reports.Count == 0)
            return text.Append(Strings.Verify_None).Append('\n').ToString();

        for (var i = 0; i < reports.Count; i++)
        {
            var r = reports[i];
            text.Append(culture, $"## {string.Format(culture, Strings.Verify_SignatureN, i + 1)} · {(r.SignerName.Length > 0 ? r.SignerName : r.FieldName)}\n\n");
            text.Append("**").Append(Verdict(r.Verdict)).Append("**\n\n");

            if (r.Certificate is { } certificate)
            {
                var kind = r.DeclaredQualified ? Strings.Verify_Qualified
                    : certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData) ? Strings.Verify_SelfSigned
                    : null;
                text.Append(culture, $"- {Line(Strings.Verify_Signer)}{r.SignerName}{(r.Email is null ? "" : $" ({r.Email})")}\n");
                text.Append(culture, $"- {Line(Strings.Verify_Issuer)}{r.Issuer}{(kind is null ? "" : " · " + kind)} · {string.Format(culture, Strings.Verify_ValidFromTo, certificate.NotBefore, certificate.NotAfter)}\n");
                if (r.CertificateStatus == CertificateStatus.NotValidAtSigningTime)
                    text.Append(culture, $"- {Strings.Verify_NotValidAtTime}\n");
            }
            if (r.TimestampTime is { } proven)
                text.Append(culture, $"- {Line(Strings.Verify_Date)}{string.Format(culture, Strings.Verify_Timestamped, proven.ToLocalTime())}\n");
            else if (r.ClaimedTime is { } claimed)
                text.Append(culture, $"- {Line(Strings.Verify_Date)}{string.Format(culture, Strings.Verify_Claimed, claimed.ToLocalTime())}\n");
            text.Append(culture, $"- {Line(Strings.Verify_Coverage)}{r.Coverage switch
            {
                SignatureCoverage.WholeDocument => Strings.Verify_CoversAll,
                SignatureCoverage.ExtendedWithSignatures => Strings.Verify_Extended,
                _ => Strings.Verify_ModifiedAfter,
            }}\n");
            if (r.Reason is { } reason)
                text.Append(culture, $"- {Line(Strings.Verify_Reason)}{reason}\n");
            if (r.Location is { } location)
                text.Append(culture, $"- {Line(Strings.Verify_Location)}{location}\n");
            text.Append(culture, $"- {Line(Strings.Verify_Format)}{(r.Format.StartsWith("PAdES", StringComparison.Ordinal) ? r.Format : $"{r.Format} ({Strings.Verify_Legacy})")}\n\n");
        }
        return text.ToString();
    }

    /// <summary>"Signed by: " in English, "Signataire : " in French.</summary>
    private static string Line(string label) => string.Format(CultureInfo.CurrentCulture, Strings.Verify_Line, label);

    public static string Verdict(SignatureVerdict verdict) => verdict switch
    {
        SignatureVerdict.Valid => Strings.Verify_Valid,
        SignatureVerdict.ValidUnverifiedIdentity => Strings.Verify_Unverified,
        SignatureVerdict.ModifiedAfterSigning => Strings.Verify_Modified,
        _ => Strings.Verify_Invalid,
    };
}
