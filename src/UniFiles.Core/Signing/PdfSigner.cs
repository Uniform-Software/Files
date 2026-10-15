using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using UniFiles.Core.Tools.Pdf;
using UniFiles.Core.Tools.Pdf.Fonts;

namespace UniFiles.Core.Signing;

/// <summary>Where the signature goes: nowhere visible, an existing empty signature field, or a new box on a page.</summary>
public abstract record SignaturePlacement
{
    public sealed record Invisible : SignaturePlacement;

    public sealed record ExistingField(string Name) : SignaturePlacement;

    public sealed record NewBox(int Page, PdfRect Rect) : SignaturePlacement;
}

public sealed record SignatureRequest
{
    public required X509Certificate2 Certificate { get; init; }

    /// <summary>Intermediate certificates to embed (the root is left out).</summary>
    public IReadOnlyList<X509Certificate2> Chain { get; init; } = [];

    public SignaturePlacement Placement { get; init; } = new SignaturePlacement.Invisible();

    public string? Reason { get; init; }

    public string? Location { get; init; }

    /// <summary>Name shown in the signature and readers' signature panel (default: the certificate's).</summary>
    public string? SignerName { get; init; }

    public DateTimeOffset SigningTime { get; init; } = DateTimeOffset.Now;

    /// <summary>Lines drawn in a visible signature (default: name, date, reason; empty: none).</summary>
    public IReadOnlyList<string>? AppearanceLines { get; init; }

    /// <summary>Handwritten signature drawn on the left of the box (or the whole box without lines).</summary>
    public Handwriting? Drawing { get; init; }

    /// <summary>Or an image of it: PNG with transparency (see <see cref="HandwritingStore"/>).</summary>
    public byte[]? HandwritingImage { get; init; }

    public TimestampProvider? Timestamp { get; init; }
}

/// <summary>
/// Signs a PDF (PAdES): adds a signature field, its appearance and a signature dictionary in an
/// incremental update, then fills /ByteRange and /Contents. Earlier signatures stay valid.
/// </summary>
public static class PdfSigner
{
    private const string RangeKey = "range";
    private const string ContentsKey = "contents";
    private const string RangePlaceholder = "[0 ********** ********** **********]";

    public static async Task<byte[]> SignAsync(byte[] pdf, SignatureRequest request, CancellationToken cancellationToken)
    {
        using var update = IncrementalUpdate.Open(pdf);
        var document = update.Document;
        var reserved = ReservedSize(request);

        var signature = new PdfDictionary(document);
        signature.Elements["/Type"] = new PdfName("/Sig");
        signature.Elements["/Filter"] = new PdfName("/Adobe.PPKLite");
        signature.Elements["/SubFilter"] = new PdfName("/ETSI.CAdES.detached");
        signature.Elements["/ByteRange"] = update.Placeholder(RangeKey, RangePlaceholder);
        signature.Elements["/Contents"] = update.Placeholder(ContentsKey, "<" + new string('0', reserved * 2) + ">");
        signature.Elements["/M"] = new PdfString(PdfDate(request.SigningTime));
        var name = request.SignerName ?? SignerName(request.Certificate);
        signature.Elements["/Name"] = new PdfString(name, PdfStringEncoding.Unicode);
        if (!string.IsNullOrWhiteSpace(request.Reason))
            signature.Elements["/Reason"] = new PdfString(request.Reason, PdfStringEncoding.Unicode);
        if (!string.IsNullOrWhiteSpace(request.Location))
            signature.Elements["/Location"] = new PdfString(request.Location, PdfStringEncoding.Unicode);
        var app = new PdfDictionary(document);
        app.Elements["/Name"] = new PdfName("/UniFiles");
        app.Elements["/REx"] = new PdfString(typeof(PdfSigner).Assembly.GetName().Version?.ToString(3) ?? "");
        var build = new PdfDictionary(document);
        build.Elements["/App"] = app;
        signature.Elements["/Prop_Build"] = build;
        document.Internals.AddObject(signature);

        AttachField(document, signature, request, name);

        var bytes = update.Write(out var positions);
        var contentsStart = (int)positions[ContentsKey];
        var contentsEnd = contentsStart + reserved * 2 + 2;
        var range = string.Create(CultureInfo.InvariantCulture, $"[0 {contentsStart} {contentsEnd} {bytes.Length - contentsEnd}]");
        Encoding.Latin1.GetBytes(range.PadRight(RangePlaceholder.Length)).CopyTo(bytes, (int)positions[RangeKey]);

        var signed = new byte[contentsStart + bytes.Length - contentsEnd];
        Array.Copy(bytes, 0, signed, 0, contentsStart);
        Array.Copy(bytes, contentsEnd, signed, contentsStart, bytes.Length - contentsEnd);
        var cms = await CadesSigner.SignAsync(signed, request.Certificate, request.Chain, request.Timestamp, cancellationToken);
        if (cms.Length > reserved)
            throw new InvalidOperationException($"Signature of {cms.Length} bytes larger than the {reserved} reserved.");
        Encoding.ASCII.GetBytes(Convert.ToHexString(cms)).CopyTo(bytes, contentsStart + 1);
        return bytes;
    }

    /// <summary>Room for the CMS: certificates, signature, and a time-stamp token (with its own certificates).</summary>
    private static int ReservedSize(SignatureRequest request)
    {
        var certificates = request.Certificate.RawData.Length + request.Chain.Sum(c => c.RawData.Length);
        var size = 2 * certificates + 4096 + (request.Timestamp is null ? 0 : 12288);
        return Math.Max(8192, (size + 1023) / 1024 * 1024);
    }

    private static void AttachField(PdfDocument document, PdfDictionary signature, SignatureRequest request, string name)
    {
        var form = PdfForms.EnsureAcroForm(document);
        form.Elements["/SigFlags"] = new PdfInteger(3); // signatures exist, append only

        if (request.Placement is SignaturePlacement.ExistingField existing)
        {
            var field = FindSignatureField(form, existing.Name)
                ?? throw new ArgumentException($"No empty signature field '{existing.Name}'.", nameof(request));
            field.Elements["/V"] = signature.Reference;
            var widget = Widget(field);
            widget.Elements["/AP"] = Appearance(document, Rect(widget), request, name);
            return;
        }

        var (pageIndex, rect) = request.Placement is SignaturePlacement.NewBox box ? (box.Page, box.Rect) : (0, new PdfRect(0, 0, 0, 0));
        var page = document.Pages[pageIndex];
        var widgetField = new PdfDictionary(document);
        widgetField.Elements["/FT"] = new PdfName("/Sig");
        widgetField.Elements["/T"] = new PdfString(UniqueName(document), PdfStringEncoding.Unicode);
        widgetField.Elements["/V"] = signature.Reference;
        widgetField.Elements["/Type"] = new PdfName("/Annot");
        widgetField.Elements["/Subtype"] = new PdfName("/Widget");
        widgetField.Elements["/F"] = new PdfInteger(132); // print + locked
        widgetField.Elements["/Rect"] = new PdfArray(document, new PdfReal(rect.Left), new PdfReal(rect.Bottom), new PdfReal(rect.Right), new PdfReal(rect.Top));
        widgetField.Elements["/P"] = page.Reference;
        widgetField.Elements["/AP"] = Appearance(document, rect, request, name);
        document.Internals.AddObject(widgetField);

        var fields = PdfForms.ResolveArray(form.Elements["/Fields"]) ?? new PdfArray(document);
        if (form.Elements["/Fields"] is not PdfReference)
            form.Elements["/Fields"] = fields;
        fields.Elements.Add(widgetField.Reference!);

        var annots = PdfForms.ResolveArray(page.Elements["/Annots"]);
        if (annots is null)
        {
            annots = new PdfArray(document);
            page.Elements["/Annots"] = annots;
        }
        annots.Elements.Add(widgetField.Reference!);
    }

    /// <summary>
    /// The normal appearance, on a transparent background: the handwritten signature on the left (or
    /// alone), the text lines on the right (or alone). Empty for an invisible signature.
    /// </summary>
    private static PdfDictionary Appearance(PdfDocument document, PdfRect rect, SignatureRequest request, string name)
    {
        double width = rect.Width, height = rect.Height;
        var content = new StringBuilder();
        var resources = new PdfDictionary(document);
        if (width > 1 && height > 1)
        {
            const double padding = 3;
            var lines = request.AppearanceLines ?? DefaultLines(request, name);
            var hasHand = request.Drawing is { IsEmpty: false } || request.HandwritingImage is not null;
            var handWidth = !hasHand ? 0 : lines.Count == 0 ? width : width * 0.45;
            if (hasHand)
            {
                var area = new PdfRect(padding, padding, handWidth - padding, height - padding);
                if (request.Drawing is { IsEmpty: false } drawing)
                    DrawStrokes(content, drawing, area);
                else
                    DrawImage(document, content, resources, request.HandwritingImage!, area);
            }
            if (lines.Count > 0)
                DrawLines(document, content, resources, lines, new PdfRect(handWidth + padding, padding, width - padding, height - padding));
        }

        var stream = new PdfDictionary(document);
        stream.Elements["/Type"] = new PdfName("/XObject");
        stream.Elements["/Subtype"] = new PdfName("/Form");
        stream.Elements["/BBox"] = new PdfArray(document, new PdfReal(0), new PdfReal(0), new PdfReal(Math.Max(0, width)), new PdfReal(Math.Max(0, height)));
        if (resources.Elements.Count > 0)
            stream.Elements["/Resources"] = resources;
        stream.CreateStream(Encoding.ASCII.GetBytes(content.ToString()));
        document.Internals.AddObject(stream);

        var appearances = new PdfDictionary(document);
        appearances.Elements["/N"] = stream.Reference;
        return appearances;
    }

    private const string Ink = "0.09 0.08 0.06";

    private static void DrawLines(PdfDocument document, StringBuilder content, PdfDictionary resources, IReadOnlyList<string> lines, PdfRect area)
    {
        var font = Figtree.Regular;
        var size = Math.Min(12, area.Height / (lines.Count * 1.2));
        foreach (var line in lines)
            if (font.Width(line, size) > area.Width)
                size = Math.Min(size, area.Width / Math.Max(1, font.Width(line, 1)));
        size = Math.Max(size, 4);
        var top = area.Bottom + (area.Height + lines.Count * size * 1.2) / 2 - size; // centered vertically

        content.Append(string.Create(CultureInfo.InvariantCulture, $"q\nBT\n{Ink} rg\n/F1 {size:0.##} Tf\n{size * 1.2:0.##} TL\n{area.Left:0.##} {top:0.##} Td\n"));
        for (var i = 0; i < lines.Count; i++)
            content.Append(PdfFontEmbedder.Hex(font, lines[i])).Append(i == 0 ? " Tj\n" : " '\n");
        content.Append("ET\nQ\n");

        var fonts = new PdfDictionary(document);
        fonts.Elements["/F1"] = PdfFontEmbedder.Embed(document, font).Reference;
        resources.Elements["/Font"] = fonts;
    }

    /// <summary>The strokes, fitted in the area with their proportions, smoothed into curves through the midpoints.</summary>
    private static void DrawStrokes(StringBuilder content, Handwriting drawing, PdfRect area)
    {
        var (w, h) = Fit(drawing.AspectRatio, area);
        var (x0, y0) = (area.Left + (area.Width - w) / 2, area.Bottom + (area.Height - h) / 2);
        (double X, double Y) P(List<double> s, int i) => (x0 + s[2 * i] * w, y0 + (1 - s[2 * i + 1]) * h);
        string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        content.Append(CultureInfo.InvariantCulture, $"q\n{Ink} RG\n1 J 1 j\n{F(Math.Max(0.6, h * 0.035))} w\n");
        foreach (var stroke in drawing.Strokes.Where(s => s.Count >= 4))
        {
            var count = stroke.Count / 2;
            var start = P(stroke, 0);
            content.Append(CultureInfo.InvariantCulture, $"{F(start.X)} {F(start.Y)} m\n");
            if (count == 2)
            {
                var end = P(stroke, 1);
                content.Append(CultureInfo.InvariantCulture, $"{F(end.X)} {F(end.Y)} l\n");
            }
            var previous = start;
            for (var i = 1; i < count - 1; i++)
            {
                var c = P(stroke, i);
                var n = P(stroke, i + 1);
                (double X, double Y) end = i == count - 2 ? n : ((c.X + n.X) / 2, (c.Y + n.Y) / 2);
                // Quadratic curve (previous → c → end) written as a cubic.
                content.Append(CultureInfo.InvariantCulture,
                    $"{F(previous.X + 2.0 / 3 * (c.X - previous.X))} {F(previous.Y + 2.0 / 3 * (c.Y - previous.Y))} {F(end.X + 2.0 / 3 * (c.X - end.X))} {F(end.Y + 2.0 / 3 * (c.Y - end.Y))} {F(end.X)} {F(end.Y)} c\n");
                previous = end;
            }
            content.Append("S\n");
        }
        content.Append("Q\n");
    }

    /// <summary>The image of the signature, its transparency kept (soft mask).</summary>
    private static void DrawImage(PdfDocument document, StringBuilder content, PdfDictionary resources, byte[] png, PdfRect area)
    {
        Tools.Images.VipsSetup.Ensure();
        using var loaded = NetVips.Image.NewFromBuffer(png);
        using var srgb = loaded.Colourspace(NetVips.Enums.Interpretation.Srgb);
        using var rgba = srgb.HasAlpha() ? srgb.Copy() : srgb.Bandjoin(255);
        using var rgb = rgba.ExtractBand(0, 3);
        using var alpha = rgba.ExtractBand(3);

        PdfDictionary Image(byte[] pixels, string colorSpace)
        {
            using var compressed = new MemoryStream();
            using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(pixels);
            var image = new PdfDictionary(document);
            image.Elements["/Type"] = new PdfName("/XObject");
            image.Elements["/Subtype"] = new PdfName("/Image");
            image.Elements["/Width"] = new PdfInteger(rgba.Width);
            image.Elements["/Height"] = new PdfInteger(rgba.Height);
            image.Elements["/ColorSpace"] = new PdfName(colorSpace);
            image.Elements["/BitsPerComponent"] = new PdfInteger(8);
            image.Elements["/Filter"] = new PdfName("/FlateDecode");
            image.CreateStream(compressed.ToArray());
            document.Internals.AddObject(image);
            return image;
        }

        var mask = Image(alpha.WriteToMemory<byte>(), "/DeviceGray");
        var picture = Image(rgb.WriteToMemory<byte>(), "/DeviceRGB");
        picture.Elements["/SMask"] = mask.Reference;
        var images = new PdfDictionary(document);
        images.Elements["/Hand"] = picture.Reference;
        resources.Elements["/XObject"] = images;

        var (w, h) = Fit((double)rgba.Width / rgba.Height, area);
        content.Append(string.Create(CultureInfo.InvariantCulture,
            $"q\n{w:0.##} 0 0 {h:0.##} {area.Left + (area.Width - w) / 2:0.##} {area.Bottom + (area.Height - h) / 2:0.##} cm\n/Hand Do\nQ\n"));
    }

    /// <summary>The largest size with this width / height ratio that fits the area.</summary>
    private static (double Width, double Height) Fit(double ratio, PdfRect area) =>
        area.Width / area.Height > ratio ? (area.Height * ratio, area.Height) : (area.Width, area.Width / ratio);

    private static List<string> DefaultLines(SignatureRequest request, string name)
    {
        var lines = new List<string> { name, request.SigningTime.LocalDateTime.ToString("g", CultureInfo.CurrentCulture) };
        if (!string.IsNullOrWhiteSpace(request.Reason))
            lines.Add(request.Reason);
        return lines;
    }

    public static string SignerName(X509Certificate2 certificate) =>
        certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false) is { Length: > 0 } simple ? simple : certificate.Subject;

    /// <summary>"D:20261015143200+02'00'"</summary>
    internal static string PdfDate(DateTimeOffset time)
    {
        var offset = time.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        return string.Create(CultureInfo.InvariantCulture,
            $"D:{time:yyyyMMddHHmmss}{sign}{Math.Abs(offset.Hours):00}'{Math.Abs(offset.Minutes):00}'");
    }

    private static string UniqueName(PdfDocument document)
    {
        var names = PdfForms.Read(document).Select(f => f.Name).ToHashSet();
        for (var i = 1; ; i++)
            if (!names.Contains($"Signature{i}"))
                return $"Signature{i}";
    }

    /// <summary>An unsigned signature field by full name ("parent.child").</summary>
    private static PdfDictionary? FindSignatureField(PdfDictionary form, string fullName)
    {
        PdfDictionary? Search(PdfArray? kids, string prefix, string? inheritedType)
        {
            foreach (var item in kids?.Elements.ToList() ?? [])
            {
                if (PdfForms.Resolve(item) is not { } field)
                    continue;
                var partial = field.Elements.GetString("/T");
                var full = string.IsNullOrEmpty(partial) ? prefix : prefix.Length == 0 ? partial : prefix + "." + partial;
                var type = field.Elements.GetName("/FT") is { Length: > 0 } t ? t : inheritedType;
                if (full == fullName && type == "/Sig" && field.Elements["/V"] is null)
                    return field;
                var childKids = PdfForms.ResolveArray(field.Elements["/Kids"]);
                if (childKids is not null && !string.IsNullOrEmpty(partial) && Search(childKids, full, type) is { } found)
                    return found;
            }
            return null;
        }
        return Search(PdfForms.ResolveArray(form.Elements["/Fields"]), "", null);
    }

    private static PdfDictionary Widget(PdfDictionary field) =>
        field.Elements.GetName("/Subtype") == "/Widget"
            ? field
            : PdfForms.Resolve(PdfForms.ResolveArray(field.Elements["/Kids"])?.Elements.FirstOrDefault()) ?? field;

    private static PdfRect Rect(PdfDictionary widget)
    {
        var array = PdfForms.ResolveArray(widget.Elements["/Rect"]);
        if (array is null || array.Elements.Count < 4)
            return new PdfRect(0, 0, 0, 0);
        var v = array.Elements.Take(4).Select(PdfForms.Number).ToArray();
        return new PdfRect(Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3]));
    }
}
