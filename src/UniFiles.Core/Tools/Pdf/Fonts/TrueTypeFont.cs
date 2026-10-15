using System.Buffers.Binary;

namespace UniFiles.Core.Tools.Pdf.Fonts;

/// <summary>
/// What UniFiles needs from a TrueType font to write text into a PDF: characters to glyphs (cmap),
/// glyph widths (hmtx) and the metrics of the font descriptor. Read-only, from the font's bytes.
/// </summary>
public sealed class TrueTypeFont
{
    private readonly Dictionary<int, ushort> _glyphs = [];
    private readonly ushort[] _advances;

    public TrueTypeFont(byte[] data, string postScriptName)
    {
        Data = data;
        PostScriptName = postScriptName;
        var tables = ReadTables(data);

        var head = tables["head"];
        UnitsPerEm = U16(data, head + 18);
        BoundingBox = (S16(data, head + 36), S16(data, head + 38), S16(data, head + 40), S16(data, head + 42));

        var hhea = tables["hhea"];
        Ascent = S16(data, hhea + 4);
        Descent = S16(data, hhea + 6);
        int metrics = U16(data, hhea + 34);

        var glyphCount = U16(data, tables["maxp"] + 4);
        _advances = new ushort[glyphCount];
        var hmtx = tables["hmtx"];
        for (var g = 0; g < glyphCount; g++)
            _advances[g] = U16(data, hmtx + 4 * Math.Min(g, metrics - 1));

        CapHeight = Ascent;
        if (tables.TryGetValue("OS/2", out var os2) && U16(data, os2) >= 2)
            CapHeight = S16(data, os2 + 88);
        if (tables.TryGetValue("post", out var post))
            ItalicAngle = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(post + 4)) / 65536.0;

        ReadCmap(data, tables["cmap"]);
    }

    public byte[] Data { get; }

    public string PostScriptName { get; }

    public int UnitsPerEm { get; }

    public int Ascent { get; }

    public int Descent { get; }

    public int CapHeight { get; }

    public double ItalicAngle { get; }

    public (int XMin, int YMin, int XMax, int YMax) BoundingBox { get; }

    public int GlyphCount => _advances.Length;

    /// <summary>Unicode code point → glyph, for every character the font draws.</summary>
    public IReadOnlyDictionary<int, ushort> Glyphs => _glyphs;

    public bool Has(int codePoint) => _glyphs.ContainsKey(codePoint);

    public ushort Glyph(int codePoint) => _glyphs.GetValueOrDefault(codePoint);

    /// <summary>Advance width of a glyph in thousandths of an em (PDF glyph space).</summary>
    public double Advance(ushort glyph) => _advances[glyph] * 1000.0 / UnitsPerEm;

    /// <summary>Width of a text at a size in points.</summary>
    public double Width(string text, double size)
    {
        var total = 0.0;
        foreach (var rune in text.EnumerateRunes())
            total += Advance(Glyph(!Has(rune.Value) && System.Text.Rune.IsWhiteSpace(rune) ? ' ' : rune.Value));
        return total * size / 1000;
    }

    private static Dictionary<string, int> ReadTables(byte[] data)
    {
        var tables = new Dictionary<string, int>();
        int count = U16(data, 4);
        for (var i = 0; i < count; i++)
        {
            var record = 12 + 16 * i;
            var tag = System.Text.Encoding.ASCII.GetString(data, record, 4);
            tables[tag] = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record + 8));
        }
        foreach (var required in new[] { "head", "hhea", "hmtx", "maxp", "cmap" })
            if (!tables.ContainsKey(required))
                throw new InvalidDataException($"TrueType font without '{required}' table.");
        return tables;
    }

    /// <summary>Unicode subtables: format 12 (all planes) when there is one, otherwise format 4 (BMP).</summary>
    private void ReadCmap(byte[] data, int cmap)
    {
        int count = U16(data, cmap + 2);
        int? format4 = null, format12 = null;
        for (var i = 0; i < count; i++)
        {
            var record = cmap + 4 + 8 * i;
            int platform = U16(data, record), encoding = U16(data, record + 2);
            var offset = cmap + (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record + 4));
            var unicode = platform == 0 || (platform == 3 && encoding is 1 or 10);
            if (!unicode)
                continue;
            switch (U16(data, offset))
            {
                case 4: format4 ??= offset; break;
                case 12: format12 ??= offset; break;
            }
        }

        if (format12 is { } f12)
        {
            var groups = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(f12 + 12));
            for (var g = 0; g < groups; g++)
            {
                var group = f12 + 16 + 12 * g;
                var start = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(group));
                var end = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(group + 4));
                var glyph = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(group + 8));
                for (var c = start; c <= end && c <= 0x10FFFF; c++)
                    Add((int)c, (ushort)(glyph + c - start));
            }
        }
        else if (format4 is { } f4)
        {
            var segments = U16(data, f4 + 6) / 2;
            var ends = f4 + 14;
            var starts = ends + 2 * segments + 2;
            var deltas = starts + 2 * segments;
            var ranges = deltas + 2 * segments;
            for (var s = 0; s < segments; s++)
            {
                int end = U16(data, ends + 2 * s), start = U16(data, starts + 2 * s);
                var delta = S16(data, deltas + 2 * s);
                int rangeOffset = U16(data, ranges + 2 * s);
                for (var c = start; c <= end && c != 0xFFFF; c++)
                {
                    int glyph;
                    if (rangeOffset == 0)
                        glyph = (c + delta) & 0xFFFF;
                    else
                    {
                        var at = ranges + 2 * s + rangeOffset + 2 * (c - start);
                        glyph = U16(data, at);
                        if (glyph != 0)
                            glyph = (glyph + delta) & 0xFFFF;
                    }
                    Add(c, (ushort)glyph);
                }
            }
        }
        else
        {
            throw new InvalidDataException("TrueType font without a Unicode cmap.");
        }
    }

    private void Add(int codePoint, ushort glyph)
    {
        if (glyph != 0 && glyph < _advances.Length)
            _glyphs.TryAdd(codePoint, glyph);
    }

    private static ushort U16(byte[] data, int at) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));

    private static short S16(byte[] data, int at) => BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(at));
}
