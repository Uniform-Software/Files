using UniFiles.Core.Tools;
using UniFiles.Core.Tools.Pdf;
using UniFiles.Core.Tools.Pdf.Fonts;

namespace UniFiles.Core.Tests;

public class FontTests
{
    [Fact]
    public void Figtree_reads_its_glyphs_and_widths()
    {
        var font = Figtree.Regular;
        Assert.Equal("Figtree-Regular", font.PostScriptName);
        Assert.True(font.UnitsPerEm > 0);
        foreach (var c in "AaÉœ€Łő")
            Assert.True(font.Has(c), $"Figtree should draw {c}");
        Assert.True(font.Width("WWW", 10) > font.Width("iii", 10));
        Assert.Equal(0, font.Width("", 10));
        // A space the font lacks (narrow no-break space of English times) is drawn as a plain space.
        Assert.Equal(PdfFontEmbedder.Hex(font, "3:42 PM"), PdfFontEmbedder.Hex(font, "3:42\u202FPM"));
    }

    [Theory]
    [InlineData("Élodie Martin — 20 €, œuvre")] // Helvetica (WinAnsi)
    [InlineData("Łódź, Ő. Kovács")]              // Figtree embedded
    public async Task Flattened_values_keep_every_character(string value)
    {
        using var temp = new TempFolder();
        var path = temp.File("form.pdf");
        using (var document = new PdfSharp.Pdf.PdfDocument())
        {
            document.AddPage();
            document.Save(path);
        }
        var tool = new PdfFormFillTool();
        var settings = ToolSettings.FromDefaults(tool);
        settings[PdfFormFillTool.ValuesKey] = PdfFormFillTool.SerializeValues(new Dictionary<string, string> { ["nom"] = value });
        settings[PdfFormFillTool.AddedKey] = PdfFormFillTool.SerializeAdded([new("nom", FormFieldKind.Text, 0, new PdfRect(72, 600, 500, 624), 12, false, null)]);
        settings[PdfFormFillTool.FlattenKey] = true;

        var output = (await tool.ExecuteAsync(new ToolContext { Inputs = [path], Settings = settings, Output = new OutputFolder(temp.Sub("out")) }, CancellationToken.None)).Single();

        using var renderer = PdfRenderer.Open(output);
        Assert.Equal(value, renderer.ExtractText(0).Trim());
    }
}
