using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Signatures;
using UniFiles.Core.Signing;
using UniFiles.Core.Tools.Pdf;

namespace UniFiles.Core.Tests;

public class PdfVerifyTests
{
    private static byte[] BlankPdf()
    {
        using var document = new PdfDocument();
        document.AddPage();
        var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    private static Task<byte[]> Sign(byte[] pdf, X509Certificate2? certificate = null, SignaturePlacement? placement = null, IReadOnlyList<X509Certificate2>? chain = null) =>
        PdfSigner.SignAsync(pdf, new SignatureRequest
        {
            Certificate = certificate ?? SigningTestKit.Certificate(),
            Chain = chain ?? [],
            Placement = placement ?? new SignaturePlacement.Invisible(),
            Reason = "Bon pour accord",
            Location = "Lyon",
        }, CancellationToken.None);

    [Fact]
    public async Task A_self_signed_signature_is_intact_but_its_identity_unverified()
    {
        var report = Assert.Single(PdfSignatureVerifier.Verify(await Sign(BlankPdf())));

        Assert.Equal(SignatureVerdict.ValidUnverifiedIdentity, report.Verdict);
        Assert.True(report.Intact);
        Assert.Equal(SignatureCoverage.WholeDocument, report.Coverage);
        Assert.Equal(CertificateStatus.UnknownIdentity, report.CertificateStatus);
        Assert.Equal("Élodie Martin", report.SignerName);
        Assert.Equal("PAdES B-B", report.Format);
        Assert.True(report.CertificateBound);
        Assert.Equal("Bon pour accord", report.Reason);
        Assert.Equal("Lyon", report.Location);
        Assert.NotNull(report.ClaimedTime);
        Assert.Null(report.TimestampTime);
        Assert.False(report.DeclaredQualified);
    }

    [Fact]
    public async Task An_organization_member_is_trusted_once_the_authority_is_accepted()
    {
        var (authority, member) = SigningTestKit.AuthorityAndMember();
        var signed = await Sign(BlankPdf(), member);

        Assert.Equal(SignatureVerdict.ValidUnverifiedIdentity, Assert.Single(PdfSignatureVerifier.Verify(signed)).Verdict);
        var trusted = Assert.Single(PdfSignatureVerifier.Verify(signed, [authority]));
        Assert.Equal(SignatureVerdict.Valid, trusted.Verdict);
        Assert.Equal("Association des Lilas", trusted.Issuer);
    }

    [Fact]
    public async Task One_changed_byte_in_the_signed_part_makes_the_signature_invalid()
    {
        var signed = await Sign(BlankPdf());
        var text = Encoding.Latin1.GetString(signed);
        var at = text.IndexOf("612", StringComparison.Ordinal); // page width, inside the signed bytes
        signed[at + 2] = (byte)'3';

        Assert.Equal(SignatureVerdict.Invalid, Assert.Single(PdfSignatureVerifier.Verify(signed)).Verdict);
    }

    [Fact]
    public async Task A_page_changed_after_signing_is_reported()
    {
        var signed = await Sign(BlankPdf());
        using var update = IncrementalUpdate.Open(signed);
        update.Document.Pages[0].Elements["/Rotate"] = new PdfInteger(90);
        var changed = update.Write(out _);

        var report = Assert.Single(PdfSignatureVerifier.Verify(changed));
        Assert.True(report.Intact);
        Assert.Equal(SignatureCoverage.ModifiedAfter, report.Coverage);
        Assert.Equal(SignatureVerdict.ModifiedAfterSigning, report.Verdict);
    }

    [Fact]
    public async Task A_second_signature_is_an_allowed_change_for_the_first()
    {
        var once = await Sign(BlankPdf(), placement: new SignaturePlacement.NewBox(0, new PdfRect(50, 50, 250, 110)));
        var twice = await Sign(once, SigningTestKit.Certificate("Léo Dupont"), new SignaturePlacement.NewBox(0, new PdfRect(300, 50, 500, 110)));

        var reports = PdfSignatureVerifier.Verify(twice);
        Assert.Equal(2, reports.Count);
        Assert.Equal(SignatureCoverage.ExtendedWithSignatures, reports[0].Coverage);
        Assert.Equal(SignatureVerdict.ValidUnverifiedIdentity, reports[0].Verdict);
        Assert.Equal(SignatureCoverage.WholeDocument, reports[1].Coverage);
        Assert.Equal("Léo Dupont", reports[1].SignerName);
    }

    [Fact]
    public async Task A_certificate_expired_at_signing_time_is_reported()
    {
        var expired = SigningTestKit.CertificateValid(DateTimeOffset.Now.AddYears(-3), DateTimeOffset.Now.AddYears(-1));
        var report = Assert.Single(PdfSignatureVerifier.Verify(await Sign(BlankPdf(), expired)));

        Assert.True(report.Intact);
        Assert.Equal(CertificateStatus.NotValidAtSigningTime, report.CertificateStatus);
        Assert.Equal(SignatureVerdict.ValidUnverifiedIdentity, report.Verdict);
    }

    [Fact]
    public void An_older_adobe_signature_is_checked_too()
    {
        // PDFsharp's own signature: adbe.pkcs7.detached, without signing-certificate-v2.
        var output = new MemoryStream();
        using (var document = new PdfDocument())
        {
            document.AddPage();
            DigitalSignatureHandler.ForDocument(document, new PdfSharpDefaultSigner(SigningTestKit.Certificate("Ancien Outil"), PdfMessageDigestType.SHA256, null),
                new DigitalSignatureOptions { PageIndex = 0, Rectangle = new XRect(10, 10, 100, 40), AppearanceHandler = new Blank() });
            document.Save(output);
        }

        var report = Assert.Single(PdfSignatureVerifier.Verify(output.ToArray()));
        Assert.True(report.Intact);
        Assert.Equal("adbe.pkcs7.detached", report.Format);
        Assert.False(report.CertificateBound);
        Assert.Equal("Ancien Outil", report.SignerName);
    }

    [Fact]
    public void A_qualified_certificate_is_recognized_from_its_qc_statements()
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            foreach (var id in new[] { "0.4.0.1862.1.1", "0.4.0.1862.1.4" })
                using (writer.PushSequence())
                    writer.WriteObjectIdentifier(id);
        }
        var qualified = SigningTestKit.CertificateValid(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1),
            new X509Extension("1.3.6.1.5.5.7.1.3", writer.Encode(), false));

        Assert.True(PdfSignatureVerifier.IsDeclaredQualified(qualified));
        Assert.False(PdfSignatureVerifier.IsDeclaredQualified(SigningTestKit.Certificate()));
    }

    [Theory]
    [InlineData("D:20261015143200+02'00'", "2026-10-15T14:32:00+02:00")]
    [InlineData("D:20261015143200Z", "2026-10-15T14:32:00+00:00")]
    [InlineData("D:2026", "2026-01-01T00:00:00+00:00")]
    public void Pdf_dates_are_read(string text, string expected)
    {
        Assert.Equal(DateTimeOffset.Parse(expected), PdfSignatureVerifier.ParseDate(text));
    }

    [Fact]
    public void Signing_dates_are_written_with_their_time_zone() =>
        Assert.Equal("D:20261015143200+02'00'", PdfSigner.PdfDate(new DateTimeOffset(2026, 10, 15, 14, 32, 0, TimeSpan.FromHours(2))));

    private sealed class Blank : PdfSharp.Pdf.Annotations.IAnnotationAppearanceHandler
    {
        public void DrawAppearance(XGraphics gfx, XRect rect) => gfx.DrawRectangle(XPens.Black, rect);
    }
}
