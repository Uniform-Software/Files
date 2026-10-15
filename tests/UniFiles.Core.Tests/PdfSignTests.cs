using System.Security.Cryptography;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UniFiles.Core.Signing;
using UniFiles.Core.Tools;
using UniFiles.Core.Tools.Pdf;

namespace UniFiles.Core.Tests;

public class PdfSignTests
{
    private static byte[] BlankPdf(int pages = 1)
    {
        using var document = new PdfDocument();
        for (var i = 0; i < pages; i++)
            document.AddPage();
        var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    private static Task<byte[]> Sign(byte[] pdf, SignaturePlacement? placement = null, string name = "Élodie Martin", bool ecdsa = false) =>
        PdfSigner.SignAsync(pdf, new SignatureRequest
        {
            Certificate = SigningTestKit.Certificate(name, ecdsa),
            Placement = placement ?? new SignaturePlacement.Invisible(),
            Reason = "Lu et approuvé",
            Location = "Paris",
        }, CancellationToken.None);

    [Fact]
    public async Task An_invisible_signature_is_pades_and_leaves_the_original_bytes_untouched()
    {
        var original = BlankPdf();
        var signed = await Sign(original);

        Assert.Equal(original, signed[..original.Length]);
        var signature = Assert.Single(SigningTestKit.Signatures(signed));
        Assert.True(signature.Valid);
        Assert.True(signature.CoversWholeFile);
        Assert.Equal("ETSI.CAdES.detached", signature.SubFilter);

        var signer = signature.Cms!.SignerInfos[0];
        Assert.Equal(CadesSigner.Sha256, signer.DigestAlgorithm.Value);
        var attributes = signer.SignedAttributes.Cast<CryptographicAttributeObject>().Select(a => a.Oid.Value).ToHashSet();
        Assert.Equal(new HashSet<string?> { "1.2.840.113549.1.9.3", "1.2.840.113549.1.9.4", CadesSigner.SigningCertificateV2 }, attributes);
        Assert.Equal("CN=Élodie Martin", signer.Certificate!.Subject);

        // Still a PDF every library reads, with the signature field in the form.
        using var reopened = PdfReader.Open(new MemoryStream(signed), PdfDocumentOpenMode.Import);
        Assert.Equal(1, reopened.PageCount);
        var field = Assert.Single(PdfForms.Read(reopened));
        Assert.Equal(FormFieldKind.Signature, field.Kind);
        Assert.Equal("Signature1", field.Name);
    }

    [Fact]
    public async Task A_visible_signature_draws_the_signer_in_its_box()
    {
        var signed = await Sign(BlankPdf(2), new SignaturePlacement.NewBox(1, new PdfRect(300, 80, 520, 150)), "Łukasz Kowalski", ecdsa: true);

        Assert.True(Assert.Single(SigningTestKit.Signatures(signed)).Valid);
        var path = Path.Combine(Path.GetTempPath(), $"uf-sign-{Guid.NewGuid():N}.pdf");
        try
        {
            // PDFium draws form widgets only with its form module: flatten a copy to see the appearance.
            File.WriteAllBytes(path, signed);
            using (var copy = PdfForms.Open(path))
            {
                PdfForms.Flatten(copy, keepSignatures: false);
                copy.Save(path);
            }
            using var renderer = PdfRenderer.Open(path);
            Assert.Contains("Łukasz Kowalski", renderer.ExtractText(1));
            Assert.Contains("Lu et approuvé", renderer.ExtractText(1));
            Assert.True(DarkPixels(renderer, 1, 300, 80, 520, 150) > 50);
            Assert.Equal(0, DarkPixels(renderer, 0, 300, 80, 520, 150));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Dark pixels inside a box given in PDF points (Letter page, rendered at 72 dpi).</summary>
    private static int DarkPixels(PdfRenderer renderer, int page, double left, double bottom, double right, double top)
    {
        var pixels = renderer.RenderAtDpi(page, 72, out var width, out var height);
        var dark = 0;
        for (var y = (int)(height - top); y < (int)(height - bottom); y++)
            for (var x = (int)left; x < (int)right; x++)
                if (pixels[(y * width + x) * 4] < 100)
                    dark++;
        return dark;
    }

    [Fact]
    public async Task A_second_signer_keeps_the_first_signature_valid()
    {
        var once = await Sign(BlankPdf(), new SignaturePlacement.NewBox(0, new PdfRect(50, 50, 250, 110)));
        var twice = await Sign(once, new SignaturePlacement.NewBox(0, new PdfRect(300, 50, 500, 110)), "Léo Dupont");

        Assert.Equal(once, twice[..once.Length]);
        var signatures = SigningTestKit.Signatures(twice);
        Assert.Equal(2, signatures.Count);
        Assert.All(signatures, s => Assert.True(s.Valid));
        Assert.False(signatures[0].CoversWholeFile);   // the first covers its own revision...
        Assert.Equal(once.Length, signatures[0].ByteRange[2] + signatures[0].ByteRange[3]);
        Assert.True(signatures[1].CoversWholeFile);    // ...the second covers everything
        using var reopened = PdfReader.Open(new MemoryStream(twice), PdfDocumentOpenMode.Import);
        Assert.Equal(["Signature1", "Signature2"], PdfForms.Read(reopened).Select(f => f.Name));
    }

    [Fact]
    public async Task A_pdf_with_a_cross_reference_stream_gets_a_stream_section()
    {
        var original = SigningTestKit.XrefStreamPdf();
        var signed = await Sign(original, new SignaturePlacement.NewBox(0, new PdfRect(50, 50, 250, 110)));

        Assert.Equal(original, signed[..original.Length]);
        Assert.True(Assert.Single(SigningTestKit.Signatures(signed)).Valid);
        var update = System.Text.Encoding.Latin1.GetString(signed, original.Length, signed.Length - original.Length);
        Assert.Contains("/Type/XRef", update);
        Assert.DoesNotContain("\ntrailer", update);
        using var reopened = PdfReader.Open(new MemoryStream(signed), PdfDocumentOpenMode.Import);
        Assert.Single(PdfForms.Read(reopened));
    }

    [Fact]
    public async Task A_pdf_with_unused_object_numbers_keeps_its_page_contents()
    {
        // Object 3 is free and the page content is object 5: PDFsharp's Modify mode would renumber
        // them on reading, and an update written with those numbers would point at the wrong objects.
        var content = "BT /F1 24 Tf 72 700 Td (Bonjour) Tj ET";
        var text = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new SortedDictionary<int, int>();
        void Obj(int n, string body) { offsets[n] = text.Length; text.Append($"{n} 0 obj\n{body}\nendobj\n"); }
        Obj(1, "<</Type/Catalog/Pages 2 0 R>>");
        Obj(2, "<</Type/Pages/Kids[4 0 R]/Count 1>>");
        Obj(4, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Contents 5 0 R/Resources<</Font<</F1 6 0 R>>>>>>");
        Obj(5, $"<</Length {content.Length}>>\nstream\n{content}\nendstream");
        Obj(6, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>");
        var xref = text.Length;
        text.Append("xref\n0 7\n0000000000 65535 f\r\n");
        for (var n = 1; n <= 6; n++)
            text.Append(offsets.TryGetValue(n, out var at) ? $"{at:D10} 00000 n\r\n" : "0000000000 00001 f\r\n");
        text.Append($"trailer\n<</Size 7/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF\n");
        var original = System.Text.Encoding.Latin1.GetBytes(text.ToString());

        var signed = await Sign(original, new SignaturePlacement.NewBox(0, new PdfRect(300, 80, 500, 140)));

        Assert.True(Assert.Single(SigningTestKit.Signatures(signed)).Valid);
        var path = Path.Combine(Path.GetTempPath(), $"uf-gaps-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, signed);
            using var renderer = PdfRenderer.Open(path);
            Assert.Contains("Bonjour", renderer.ExtractText(0));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task An_empty_signature_field_is_filled_in_place()
    {
        byte[] original;
        using (var document = new PdfDocument())
        {
            var page = document.AddPage();
            var form = PdfForms.EnsureAcroForm(document);
            var field = new PdfDictionary(document);
            field.Elements["/FT"] = new PdfName("/Sig");
            field.Elements["/T"] = new PdfString("signature_parent");
            field.Elements["/Type"] = new PdfName("/Annot");
            field.Elements["/Subtype"] = new PdfName("/Widget");
            field.Elements["/Rect"] = new PdfArray(document, new PdfReal(100), new PdfReal(100), new PdfReal(300), new PdfReal(150));
            field.Elements["/P"] = page.Reference;
            document.Internals.AddObject(field);
            PdfForms.ResolveArray(form.Elements["/Fields"])!.Elements.Add(field.Reference!);
            page.Elements["/Annots"] = new PdfArray(document, field.Reference!);
            var output = new MemoryStream();
            document.Save(output);
            original = output.ToArray();
        }

        var signed = await Sign(original, new SignaturePlacement.ExistingField("signature_parent"));

        Assert.True(Assert.Single(SigningTestKit.Signatures(signed)).Valid);
        using var reopened = PdfReader.Open(new MemoryStream(signed), PdfDocumentOpenMode.Import);
        Assert.Equal("signature_parent", Assert.Single(PdfForms.Read(reopened)).Name);
        await Assert.ThrowsAsync<ArgumentException>(() => Sign(signed, new SignaturePlacement.ExistingField("signature_parent")));
    }

    [Fact]
    public async Task An_encrypted_pdf_is_refused()
    {
        var output = new MemoryStream();
        using (var document = new PdfDocument())
        {
            document.AddPage();
            document.SecuritySettings.OwnerPassword = "owner";
            document.Save(output);
        }
        await Assert.ThrowsAsync<ToolException>(() => Sign(output.ToArray()));
    }

    [Fact]
    public async Task A_timestamp_token_is_added_as_an_unsigned_attribute()
    {
        byte[]? digest = null;
        var signed = await PdfSigner.SignAsync(BlankPdf(), new SignatureRequest
        {
            Certificate = SigningTestKit.Certificate(),
            Timestamp = (hash, _) =>
            {
                digest = hash;
                return Task.FromResult(new byte[] { 0x30, 0x03, 0x02, 0x01, 0x01 }); // stand-in token: the real one is checked in TimestampTests
            },
        }, CancellationToken.None);

        var signer = Assert.Single(SigningTestKit.Signatures(signed)).Cms!.SignerInfos[0];
        Assert.Equal(SHA256.HashData(signer.GetSignature()), digest);
        Assert.Contains(signer.UnsignedAttributes.Cast<CryptographicAttributeObject>(), a => a.Oid.Value == CadesSigner.SignatureTimeStampToken);
    }
}
