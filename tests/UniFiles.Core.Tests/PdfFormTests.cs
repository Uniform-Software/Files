using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UniFiles.Core.Tools;
using UniFiles.Core.Tools.Pdf;

namespace UniFiles.Core.Tests;

public class PdfFormTests
{
    private const double PageHeight = 792; // PDFsharp's default page: Letter

    private static PdfRect Box(double x, double yTop, double width, double height) =>
        new(x, PageHeight - yTop - height, x + width, PageHeight - yTop);

    private static string BlankPdf(TempFolder temp, string name = "blank.pdf", int pages = 1)
    {
        var path = temp.File(name);
        using var document = new PdfDocument();
        for (var i = 0; i < pages; i++)
            document.AddPage();
        document.Save(path);
        return path;
    }

    /// <summary>A sign-up form made with UniFiles' own fields: name, comment, adult check box.</summary>
    private static string SignUpForm(TempFolder temp, string name = "form.pdf")
    {
        var path = BlankPdf(temp, name);
        using var document = PdfForms.Open(path);
        PdfForms.Apply(document, new Dictionary<string, string>(),
        [
            new("nom", FormFieldKind.Text, 0, Box(140, 86, 250, 20), 0, true, null),
            new("commentaire", FormFieldKind.MultilineText, 0, Box(140, 146, 300, 80), 11, false, null),
            new("majeur", FormFieldKind.CheckBox, 0, Box(140, 266, 16, 16), 0, false, null),
        ], flatten: false);
        document.Save(path);
        return path;
    }

    private static async Task<string> Fill(TempFolder temp, string input, Dictionary<string, string> values, bool flatten = false, List<NewFormField>? added = null)
    {
        var tool = new PdfFormFillTool();
        var settings = ToolSettings.FromDefaults(tool);
        settings[PdfFormFillTool.ValuesKey] = PdfFormFillTool.SerializeValues(values);
        settings[PdfFormFillTool.AddedKey] = PdfFormFillTool.SerializeAdded(added ?? []);
        settings[PdfFormFillTool.FlattenKey] = flatten;
        var outputs = await tool.ExecuteAsync(new ToolContext { Inputs = [input], Settings = settings, Output = new OutputFolder(temp.Sub("out")) }, CancellationToken.None);
        return outputs.Single();
    }

    [Fact]
    public async Task Added_fields_can_be_filled_and_read_back_with_accents()
    {
        using var temp = new TempFolder();
        var form = SignUpForm(temp);

        var fields = PdfForms.Read(form);
        Assert.Equal(["nom", "commentaire", "majeur"], fields.Select(f => f.Name));
        Assert.Equal([FormFieldKind.Text, FormFieldKind.MultilineText, FormFieldKind.CheckBox], fields.Select(f => f.Kind));
        Assert.True(fields[0].Required);

        var filled = await Fill(temp, form, new()
        {
            ["nom"] = "Élodie Martin-Dupré (€, œ)",
            ["commentaire"] = "Ligne 1\nLigne 2 : àéèçù",
            ["majeur"] = PdfForms.Checked,
        });

        var read = PdfForms.Read(filled).ToDictionary(f => f.Name, f => f.Value);
        Assert.Equal("Élodie Martin-Dupré (€, œ)", read["nom"]);
        Assert.Equal("Ligne 1\nLigne 2 : àéèçù", read["commentaire"]);
        Assert.Equal(PdfForms.Checked, read["majeur"]);
        Assert.StartsWith("form - ", Path.GetFileName(filled));
    }

    [Fact]
    public async Task Flattening_keeps_the_answers_on_the_page_and_removes_the_fields()
    {
        using var temp = new TempFolder();
        var form = SignUpForm(temp);

        var flat = await Fill(temp, form, new() { ["nom"] = "Zoé", ["majeur"] = PdfForms.Checked }, flatten: true);

        Assert.Empty(PdfForms.Read(flat));
        // The value was drawn into the page: PDFium (no form support needed) shows dark pixels in the name box.
        using var renderer = PdfRenderer.Open(flat);
        var pixels = renderer.Render(0, 792, out var width, out _);
        var dark = 0;
        for (var y = 88; y < 104; y++)
            for (var x = 142; x < 200; x++)
                if (pixels[(y * width + x) * 4] < 128)
                    dark++;
        Assert.True(dark > 10, $"Expected text in the flattened field, found {dark} dark pixels.");
    }

    [Fact]
    public async Task Flatten_quick_action_refuses_a_pdf_without_fields()
    {
        using var temp = new TempFolder();
        var blank = BlankPdf(temp);
        await Assert.ThrowsAsync<ToolException>(() => new PdfFormFlattenTool().ExecuteAsync(
            new ToolContext { Inputs = [blank], Settings = new ToolSettings(), Output = new OutputFolder(temp.Sub("out")) }, CancellationToken.None));
    }

    [Fact]
    public async Task Fields_can_be_added_from_the_editor_settings()
    {
        using var temp = new TempFolder();
        var blank = BlankPdf(temp);

        var output = await Fill(temp, blank, new() { ["email"] = "a@b.fr" }, added:
        [
            new("email", FormFieldKind.Text, 0, Box(100, 100, 200, 20), 10, false, null),
            new("accord", FormFieldKind.CheckBox, 0, Box(100, 140, 14, 14), 0, true, null),
        ]);

        var fields = PdfForms.Read(output);
        Assert.Equal(["email", "accord"], fields.Select(f => f.Name));
        Assert.Equal("a@b.fr", fields[0].Value);
        Assert.Equal(PdfForms.Unchecked, fields[1].Value);
        Assert.Equal(10, fields[0].FontSize);
    }

    [Fact]
    public async Task A_name_already_used_is_refused()
    {
        using var temp = new TempFolder();
        var form = SignUpForm(temp);
        await Assert.ThrowsAsync<ToolException>(() => Fill(temp, form, new(), added:
            [new("nom", FormFieldKind.Text, 0, Box(10, 10, 50, 20), 0, false, null)]));
    }

    [Fact]
    public async Task Answers_of_several_forms_export_to_one_csv_for_excel()
    {
        using var temp = new TempFolder();
        var first = await Fill(temp, SignUpForm(temp, "a.pdf"), new() { ["nom"] = "Ana; \"la reine\"", ["majeur"] = PdfForms.Checked });
        var second = await Fill(temp, SignUpForm(temp, "b.pdf"), new() { ["nom"] = "Léo", ["commentaire"] = "deux\nlignes" });
        var tool = new PdfFormExportTool();
        var settings = ToolSettings.FromDefaults(tool);
        settings["separator"] = "semicolon";
        settings["checkboxes"] = "digits";

        var csvPath = (await tool.ExecuteAsync(new ToolContext { Inputs = [first, second], Settings = settings, Output = new OutputFolder(temp.Sub("csv")) }, CancellationToken.None)).Single();

        var bytes = File.ReadAllBytes(csvPath);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]).Replace("\r\n", "\n");
        var lines = text.Split('\n');
        Assert.EndsWith(";nom;commentaire;majeur", lines[0]);
        Assert.Contains("\"Ana; \"\"la reine\"\"\";;1", text);
        Assert.Contains("Léo;\"deux\nlignes\";0", text);
    }

    [Fact]
    public void Page_geometry_reports_rotation()
    {
        using var temp = new TempFolder();
        var path = temp.File("rotated.pdf");
        using (var document = new PdfDocument())
        {
            document.AddPage();
            document.AddPage().Rotate = 90;
            document.Save(path);
        }

        using var opened = PdfForms.Open(path);
        var pages = PdfForms.Pages(opened);
        Assert.Equal([0, 90], pages.Select(p => p.Rotate));
        Assert.Equal(612, pages[1].MediaBox.Width);
    }

    [Fact]
    public async Task A_radio_group_selects_one_button()
    {
        using var temp = new TempFolder();
        var path = temp.File("radio.pdf");
        using (var document = new PdfDocument())
        {
            var page = document.AddPage();
            var form = new PdfDictionary(document);
            document.Internals.AddObject(form);
            document.Internals.Catalog.Elements["/AcroForm"] = form.Reference;
            var group = new PdfDictionary(document);
            group.Elements["/FT"] = new PdfName("/Btn");
            group.Elements["/Ff"] = new PdfInteger(1 << 15);
            group.Elements["/T"] = new PdfString("taille");
            group.Elements["/V"] = new PdfName("/Off");
            document.Internals.AddObject(group);
            var kids = new PdfArray(document);
            var annots = new PdfArray(document);
            foreach (var (state, x) in new[] { ("S", 100.0), ("M", 140.0) })
            {
                var widget = new PdfDictionary(document);
                widget.Elements["/Type"] = new PdfName("/Annot");
                widget.Elements["/Subtype"] = new PdfName("/Widget");
                widget.Elements["/Parent"] = group.Reference;
                widget.Elements["/Rect"] = new PdfArray(document, new PdfReal(x), new PdfReal(600), new PdfReal(x + 14), new PdfReal(614));
                widget.Elements["/P"] = page.Reference;
                var on = new PdfDictionary(document);
                on.CreateStream(Encoding.ASCII.GetBytes("0 g 2 2 10 10 re f"));
                on.Elements["/BBox"] = new PdfArray(document, new PdfReal(0), new PdfReal(0), new PdfReal(14), new PdfReal(14));
                document.Internals.AddObject(on);
                var off = new PdfDictionary(document);
                off.CreateStream([]);
                off.Elements["/BBox"] = new PdfArray(document, new PdfReal(0), new PdfReal(0), new PdfReal(14), new PdfReal(14));
                document.Internals.AddObject(off);
                var states = new PdfDictionary(document);
                states.Elements["/" + state] = on.Reference;
                states.Elements["/Off"] = off.Reference;
                var appearances = new PdfDictionary(document);
                appearances.Elements["/N"] = states;
                widget.Elements["/AP"] = appearances;
                widget.Elements["/AS"] = new PdfName("/Off");
                document.Internals.AddObject(widget);
                kids.Elements.Add(widget.Reference!);
                annots.Elements.Add(widget.Reference!);
            }
            group.Elements["/Kids"] = kids;
            page.Elements["/Annots"] = annots;
            form.Elements["/Fields"] = new PdfArray(document, group.Reference!);
            document.Save(path);
        }

        var field = Assert.Single(PdfForms.Read(path));
        Assert.Equal(FormFieldKind.Radio, field.Kind);
        Assert.Equal(["S", "M"], field.Options);
        Assert.Equal(2, field.Widgets.Count);

        var filled = await Fill(temp, path, new() { ["taille"] = "M" });
        Assert.Equal("M", Assert.Single(PdfForms.Read(filled)).Value);
    }

    [Fact]
    public void A_dynamic_xfa_form_is_refused_with_a_clear_message()
    {
        using var temp = new TempFolder();
        var path = BlankPdf(temp, "xfa.pdf");
        using (var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            var form = new PdfDictionary(document);
            form.Elements["/Fields"] = new PdfArray(document);
            form.Elements["/XFA"] = new PdfString("<xdp/>");
            document.Internals.AddObject(form);
            document.Internals.Catalog.Elements["/AcroForm"] = form.Reference;
            document.Save(path);
        }

        using var opened = PdfForms.Open(path);
        Assert.True(PdfForms.IsDynamicXfa(opened));
        Assert.Throws<ToolException>(() => PdfForms.Apply(opened, new Dictionary<string, string>(), [], flatten: false));
    }
}
