using System.Globalization;
using System.IO.Compression;
using System.Text;
using PdfSharp.Pdf;

namespace UniFiles.Core.Tools.Pdf.Fonts;

/// <summary>
/// Writes a TrueType font into a PDF as a Unicode font (Type0, Identity-H): text is shown as glyph
/// numbers, any character the font has can be drawn, and a ToUnicode map keeps the text searchable
/// and copyable. The whole font file is embedded (Figtree: about 40 KB).
/// </summary>
public static class PdfFontEmbedder
{
    /// <summary>Creates the font objects in the document and returns the Type0 font dictionary (indirect).</summary>
    public static PdfDictionary Embed(PdfDocument document, TrueTypeFont font)
    {
        var name = new PdfName("/" + font.PostScriptName);
        double Scale(int units) => Math.Round(units * 1000.0 / font.UnitsPerEm);

        var file = Stream(document, font.Data);
        file.Elements["/Length1"] = new PdfInteger(font.Data.Length);

        var descriptor = new PdfDictionary(document);
        descriptor.Elements["/Type"] = new PdfName("/FontDescriptor");
        descriptor.Elements["/FontName"] = name;
        descriptor.Elements["/Flags"] = new PdfInteger(32); // nonsymbolic
        var (xMin, yMin, xMax, yMax) = font.BoundingBox;
        descriptor.Elements["/FontBBox"] = new PdfArray(document,
            new PdfReal(Scale(xMin)), new PdfReal(Scale(yMin)), new PdfReal(Scale(xMax)), new PdfReal(Scale(yMax)));
        descriptor.Elements["/ItalicAngle"] = new PdfReal(font.ItalicAngle);
        descriptor.Elements["/Ascent"] = new PdfReal(Scale(font.Ascent));
        descriptor.Elements["/Descent"] = new PdfReal(Scale(font.Descent));
        descriptor.Elements["/CapHeight"] = new PdfReal(Scale(font.CapHeight));
        descriptor.Elements["/StemV"] = new PdfInteger(80);
        descriptor.Elements["/FontFile2"] = file.Reference;
        document.Internals.AddObject(descriptor);

        var widths = new PdfArray(document);
        for (var glyph = 0; glyph < font.GlyphCount; glyph++)
            widths.Elements.Add(new PdfInteger((int)Math.Round(font.Advance((ushort)glyph))));
        var systemInfo = new PdfDictionary(document);
        systemInfo.Elements["/Registry"] = new PdfString("Adobe");
        systemInfo.Elements["/Ordering"] = new PdfString("Identity");
        systemInfo.Elements["/Supplement"] = new PdfInteger(0);

        var cidFont = new PdfDictionary(document);
        cidFont.Elements["/Type"] = new PdfName("/Font");
        cidFont.Elements["/Subtype"] = new PdfName("/CIDFontType2");
        cidFont.Elements["/BaseFont"] = name;
        cidFont.Elements["/CIDSystemInfo"] = systemInfo;
        cidFont.Elements["/FontDescriptor"] = descriptor.Reference;
        cidFont.Elements["/W"] = new PdfArray(document, new PdfInteger(0), widths);
        cidFont.Elements["/CIDToGIDMap"] = new PdfName("/Identity");
        document.Internals.AddObject(cidFont);

        var type0 = new PdfDictionary(document);
        type0.Elements["/Type"] = new PdfName("/Font");
        type0.Elements["/Subtype"] = new PdfName("/Type0");
        type0.Elements["/BaseFont"] = name;
        type0.Elements["/Encoding"] = new PdfName("/Identity-H");
        type0.Elements["/DescendantFonts"] = new PdfArray(document, cidFont.Reference!);
        type0.Elements["/ToUnicode"] = Stream(document, Encoding.ASCII.GetBytes(ToUnicode(font))).Reference;
        document.Internals.AddObject(type0);
        return type0;
    }

    /// <summary>A text as a hex string of glyph numbers, for Tj ("&lt;0012003A&gt;").</summary>
    public static string Hex(TrueTypeFont font, string text)
    {
        var builder = new StringBuilder("<");
        foreach (var rune in text.EnumerateRunes())
        {
            // Spaces the font lacks (narrow no-break space in English times, "15:42 PM") as a plain space.
            var codePoint = !font.Has(rune.Value) && Rune.IsWhiteSpace(rune) ? ' ' : rune.Value;
            builder.Append(font.Glyph(codePoint).ToString("X4", CultureInfo.InvariantCulture));
        }
        return builder.Append('>').ToString();
    }

    /// <summary>Whether every character of the text can be drawn with the font.</summary>
    public static bool CanDraw(TrueTypeFont font, string text) =>
        text.EnumerateRunes().All(r => Rune.IsWhiteSpace(r) || font.Has(r.Value));

    /// <summary>Glyph → Unicode for every character of the font (lowest code point when glyphs are shared).</summary>
    private static string ToUnicode(TrueTypeFont font)
    {
        var map = new SortedDictionary<ushort, int>();
        foreach (var (codePoint, glyph) in font.Glyphs.OrderBy(g => g.Key))
            map.TryAdd(glyph, codePoint);

        var cmap = new StringBuilder("""
            /CIDInit /ProcSet findresource begin
            12 dict begin
            begincmap
            /CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def
            /CMapName /Adobe-Identity-UCS def
            /CMapType 2 def
            1 begincodespacerange
            <0000> <FFFF>
            endcodespacerange

            """);
        foreach (var chunk in map.Chunk(100))
        {
            cmap.Append(CultureInfo.InvariantCulture, $"{chunk.Length} beginbfchar\n");
            foreach (var (glyph, codePoint) in chunk)
            {
                var utf16 = string.Concat(char.ConvertFromUtf32(codePoint).Select(c => ((int)c).ToString("X4", CultureInfo.InvariantCulture)));
                cmap.Append(CultureInfo.InvariantCulture, $"<{glyph:X4}> <{utf16}>\n");
            }
            cmap.Append("endbfchar\n");
        }
        cmap.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return cmap.ToString();
    }

    /// <summary>A Flate-compressed indirect stream.</summary>
    private static PdfDictionary Stream(PdfDocument document, byte[] data)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data);
        var stream = new PdfDictionary(document);
        stream.CreateStream(compressed.ToArray());
        stream.Elements["/Filter"] = new PdfName("/FlateDecode");
        document.Internals.AddObject(stream);
        return stream;
    }
}
