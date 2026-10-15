using NetVips;
using PdfSharp.Pdf;
using UniFiles.Core.Signing;
using UniFiles.Core.Tools;
using UniFiles.Core.Tools.Pdf;

namespace UniFiles.Core.Tests;

/// <summary>The "Sign a PDF" and "Check signatures" actions, with identities in memory.</summary>
[Collection(nameof(SigningContextCollection))]
public sealed class PdfSignToolTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly SigningIdentity _identity;

    public PdfSignToolTests()
    {
        SigningContext.Identities = new IdentityStore(new MemoryCertificateStore(), _temp.Sub("signature"));
        SigningContext.Handwriting = new HandwritingStore(_temp.Sub("signature"));
        _identity = SigningContext.Identities.CreatePersonal("Élodie Martin", "elodie@exemple.fr");
    }

    public void Dispose() => _temp.Dispose();

    private string BlankPdf()
    {
        var path = _temp.File("contrat.pdf");
        using var document = new PdfDocument();
        document.AddPage();
        document.Save(path);
        return path;
    }

    private async Task<string> Sign(string input, Action<ToolSettings> configure)
    {
        var tool = new PdfSignTool();
        var settings = ToolSettings.FromDefaults(tool);
        settings[PdfSignTool.IdentityKey] = _identity.Id;
        settings[PdfSignTool.ReasonKey] = "Lu et approuvé";
        configure(settings);
        return (await tool.ExecuteAsync(new ToolContext { Inputs = [input], Settings = settings, Output = new OutputFolder(_temp.Sub("out")) }, CancellationToken.None)).Single();
    }

    private static int DarkPixelsAfterFlattening(string signed, double left, double bottom, double right, double top)
    {
        var copy = signed + ".flat.pdf";
        using (var document = PdfForms.Open(signed))
        {
            PdfForms.Flatten(document, keepSignatures: false);
            document.Save(copy);
        }
        using var renderer = PdfRenderer.Open(copy);
        var pixels = renderer.RenderAtDpi(0, 72, out var width, out var height);
        var dark = 0;
        for (var y = (int)(height - top); y < (int)(height - bottom); y++)
            for (var x = (int)left; x < (int)right; x++)
                if (pixels[(y * width + x) * 4] < 120)
                    dark++;
        return dark;
    }

    [Fact]
    public void Placements_are_written_and_read_back()
    {
        foreach (var placement in new SignaturePlacement[]
        {
            new SignaturePlacement.Invisible(),
            new SignaturePlacement.ExistingField("signature.client"),
            new SignaturePlacement.NewBox(2, new PdfRect(10.5, 20, 210.25, 80)),
        })
            Assert.Equal(placement, PdfSignTool.ParsePlacement(PdfSignTool.Placement(placement)));
    }

    [Fact]
    public async Task A_drawn_signature_is_signed_in_its_box_and_reported_by_the_check()
    {
        SigningContext.Handwriting.SaveDrawing(_identity.Id, new Handwriting
        {
            AspectRatio = 3,
            Strokes = [[0.05, 0.8, 0.2, 0.2, 0.35, 0.7, 0.5, 0.3, 0.7, 0.75, 0.95, 0.4], [0.1, 0.9, 0.9, 0.9]],
        });

        var signed = await Sign(BlankPdf(), s =>
        {
            s[PdfSignTool.PlacementKey] = PdfSignTool.Placement(new SignaturePlacement.NewBox(0, new PdfRect(300, 80, 540, 150)));
            s[PdfSignTool.HandwritingKey] = "drawn";
        });

        Assert.EndsWith($" - {UniFiles.Core.Resources.Strings.Suffix_Signed}.pdf", signed);
        Assert.True(DarkPixelsAfterFlattening(signed, 300, 80, 400, 150) > 40, "the drawing, on the left");
        Assert.True(DarkPixelsAfterFlattening(signed, 410, 80, 540, 150) > 40, "the text, on the right");

        var report = PdfVerifyTool.Report([signed]);
        Assert.Contains("Élodie Martin (elodie@exemple.fr)", report);
        Assert.Contains("Lu et approuvé", report);
        Assert.Contains("PAdES B-B", report);
    }

    [Fact]
    public async Task An_imported_signature_image_keeps_only_the_ink()
    {
        var scan = _temp.File("scan.jpg");
        using (var black = Image.Black(600, 300))
        using (var paper = (black + 245).Cast(Enums.BandFormat.Uchar))                                    // light grey paper
        using (var ink = paper.Mutate(m => m.DrawRect([20], 100, 120, 400, 30, fill: true)))             // a dark stroke
            ink.WriteToFile(scan);
        SigningContext.Handwriting.ImportImage(_identity.Id, scan);

        using (var prepared = Image.NewFromBuffer(SigningContext.Handwriting.Image(_identity.Id)!))
        {
            Assert.True(prepared.HasAlpha());           // transparent around the ink
            Assert.InRange(prepared.Width, 395, 405);  // cropped to the stroke
            Assert.InRange(prepared.Height, 25, 35);
        }

        var signed = await Sign(BlankPdf(), s =>
        {
            s[PdfSignTool.PlacementKey] = PdfSignTool.Placement(new SignaturePlacement.NewBox(0, new PdfRect(100, 100, 300, 160)));
            s[PdfSignTool.HandwritingKey] = "image";
            s[PdfSignTool.ShowNameKey] = false;
            s[PdfSignTool.ShowDateKey] = false;
            s[PdfSignTool.ShowReasonKey] = false;
        });
        Assert.True(DarkPixelsAfterFlattening(signed, 100, 100, 300, 160) > 100);
        Assert.Equal(SignatureVerdict.ValidUnverifiedIdentity, Assert.Single(PdfSignatureVerifier.Verify(await File.ReadAllBytesAsync(signed))).Verdict);
    }

    [Fact]
    public async Task A_missing_certificate_is_a_clear_error()
    {
        var error = await Assert.ThrowsAsync<ToolException>(() => Sign(BlankPdf(), s => s[PdfSignTool.IdentityKey] = "0000"));
        Assert.Equal(UniFiles.Core.Resources.Strings.Sign_IdentityMissing, error.Message);
    }

    [Fact]
    public async Task A_form_is_flattened_before_signing_and_its_signature_field_kept()
    {
        var form = _temp.File("inscription.pdf");
        using (var document = new PdfDocument())
        {
            document.AddPage();
            PdfForms.Apply(document, new Dictionary<string, string> { ["nom"] = "Zoé" },
                [new NewFormField("nom", FormFieldKind.Text, 0, new PdfRect(72, 700, 300, 720), 12, false, null)], flatten: false);
            document.Save(form);
        }

        var signed = await Sign(form, s =>
        {
            s[PdfSignTool.FlattenKey] = true;
            s[PdfSignTool.PlacementKey] = PdfSignTool.Placement(new SignaturePlacement.NewBox(0, new PdfRect(300, 80, 540, 150)));
        });

        using var reopened = PdfSharp.Pdf.IO.PdfReader.Open(signed, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        var field = Assert.Single(PdfForms.Read(reopened));  // "nom" is now drawn in the page
        Assert.Equal(FormFieldKind.Signature, field.Kind);
        Assert.True(PdfForms.IsSigned(reopened));
        using var renderer = PdfRenderer.Open(signed);
        Assert.Contains("Zoé", renderer.ExtractText(0));

        // Flattening or filling it now would break the signature: refused.
        var flatten = new PdfFormFlattenTool();
        await Assert.ThrowsAsync<ToolException>(() => flatten.ExecuteAsync(
            new ToolContext { Inputs = [signed], Settings = new ToolSettings(), Output = new OutputFolder(_temp.Sub("flat")) }, CancellationToken.None));
    }

    [Fact]
    public async Task A_signature_field_added_in_the_form_editor_is_signed_in_place()
    {
        var fill = new PdfFormFillTool();
        var settings = ToolSettings.FromDefaults(fill);
        settings[PdfFormFillTool.ValuesKey] = PdfFormFillTool.SerializeValues(new Dictionary<string, string>());
        settings[PdfFormFillTool.AddedKey] = PdfFormFillTool.SerializeAdded([new NewFormField("signature_1", FormFieldKind.Signature, 0, new PdfRect(330, 80, 540, 160), 0, true, null)]);
        var form = (await fill.ExecuteAsync(new ToolContext { Inputs = [BlankPdf()], Settings = settings, Output = new OutputFolder(_temp.Sub("form")) }, CancellationToken.None)).Single();
        var empty = Assert.Single(PdfForms.Read(form));
        Assert.Equal(FormFieldKind.Signature, empty.Kind);
        using (var document = PdfForms.Open(form))
            Assert.False(PdfForms.IsSigned(document));

        var signed = await Sign(form, s => s[PdfSignTool.PlacementKey] = PdfSignTool.Placement(new SignaturePlacement.ExistingField("signature_1")));

        var report = Assert.Single(PdfSignatureVerifier.Verify(await File.ReadAllBytesAsync(signed)));
        Assert.Equal("signature_1", report.FieldName);
        Assert.True(report.Intact);
    }

    [Fact]
    public void The_check_says_when_a_pdf_has_no_signature()
    {
        Assert.Contains(UniFiles.Core.Resources.Strings.Verify_None, PdfVerifyTool.Report([BlankPdf()]));
    }
}

/// <summary>Tests that replace <see cref="SigningContext"/> run one after the other.</summary>
[CollectionDefinition(nameof(SigningContextCollection), DisableParallelization = true)]
public sealed class SigningContextCollection;
