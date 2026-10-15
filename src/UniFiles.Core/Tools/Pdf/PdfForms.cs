using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using UniFiles.Core.Resources;

namespace UniFiles.Core.Tools.Pdf;

public enum FormFieldKind
{
    Text,
    MultilineText,
    CheckBox,
    Radio,
    Choice,
    Signature,
    Unsupported,
}

/// <summary>A rectangle in PDF points, origin bottom-left of the page (unrotated).</summary>
public readonly record struct PdfRect(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;

    public double Height => Top - Bottom;
}

/// <summary>Where a field shows: a page and a rectangle; for a radio button, the state it selects.</summary>
public sealed record FormWidget(int Page, PdfRect Rect, string? State);

/// <summary>
/// A form field as shown to the user. <see cref="Options"/>: choices of a list, or the states of a
/// radio group. <see cref="Widgets"/>: every place it shows (a radio group has one per button).
/// </summary>
public sealed record FormField(
    string Name,
    FormFieldKind Kind,
    int Page,
    PdfRect Rect,
    string? Value,
    IReadOnlyList<string> Options,
    bool ReadOnly,
    bool Required,
    double FontSize = 0)
{
    public IReadOnlyList<FormWidget> Widgets { get; init; } = [];
}

/// <summary>Size and orientation of a page: its media box (unrotated, in points) and its /Rotate (0, 90, 180, 270).</summary>
public sealed record PdfPageGeometry(PdfRect MediaBox, int Rotate);

/// <summary>A field added in the editor (text, multiline text or check box).</summary>
public sealed record NewFormField(string Name, FormFieldKind Kind, int Page, PdfRect Rect, double FontSize, bool Required, string? Value);

/// <summary>
/// AcroForm fields: read, fill, add and flatten. Works on PDFsharp's object model, and draws the
/// field appearances itself (Helvetica, a standard PDF font every reader has) so the values show
/// in every reader, including those that don't redraw fields (macOS Preview, mobile readers).
/// Check box value: "true" or "false". Radio value: the name of the chosen state.
/// </summary>
public static class PdfForms
{
    public const string Checked = "true";
    public const string Unchecked = "false";

    private const int FlagReadOnly = 1;
    private const int FlagRequired = 2;
    private const int FlagMultiline = 1 << 12;
    private const int FlagRadio = 1 << 15;
    private const int FlagPushButton = 1 << 16;
    private const int AnnotationHidden = 2;

    // ---- Reading ------------------------------------------------------------------------------

    /// <summary>Opens a PDF for form work. Throws a user-readable error for unreadable or protected files.</summary>
    public static PdfDocument Open(string path)
    {
        try
        {
            return PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        }
        catch (Exception ex) when (ex is PdfReaderException or InvalidOperationException && ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException(Strings.Error_PdfProtected, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ToolException(Strings.Error_PdfUnreadable, ex);
        }
    }

    public static IReadOnlyList<FormField> Read(string path)
    {
        using var document = Open(path);
        return Read(document);
    }

    public static IReadOnlyList<FormField> Read(PdfDocument document)
    {
        var fields = new List<FormField>();
        foreach (var field in Terminals(document))
        {
            var widgets = Widgets(field).ToList();
            var first = widgets.FirstOrDefault();
            var kind = Kind(field);
            var flags = Flags(field);
            fields.Add(new FormField(
                FullName(field),
                kind,
                first is null ? 0 : PageIndex(document, first),
                first is null ? default : Rect(first),
                ValueText(field, kind),
                kind switch
                {
                    FormFieldKind.Choice => ChoiceOptions(field),
                    FormFieldKind.Radio => widgets.Select(OnState).OfType<string>().Distinct().ToList(),
                    _ => [],
                },
                (flags & FlagReadOnly) != 0,
                (flags & FlagRequired) != 0,
                FontSize(field))
            {
                Widgets = widgets.Select(w => new FormWidget(PageIndex(document, w), Rect(w), OnState(w))).ToList(),
            });
        }
        return fields;
    }

    /// <summary>Each page's media box and rotation, to place the fields over a drawing of the page.</summary>
    public static IReadOnlyList<PdfPageGeometry> Pages(PdfDocument document) =>
        document.Pages.Cast<PdfPage>().Select(page =>
        {
            var box = page.MediaBox;
            return new PdfPageGeometry(new PdfRect(box.X1, box.Y1, box.X2, box.Y2), ((page.Rotate % 360) + 360) % 360);
        }).ToList();

    /// <summary>Dynamic XFA forms (LiveCycle) keep their fields outside AcroForm: not supported.</summary>
    public static bool IsDynamicXfa(PdfDocument document) =>
        AcroForm(document) is { } form && form.Elements.ContainsKey("/XFA") && !Terminals(document).Any();

    // ---- Filling and adding ---------------------------------------------------------------------

    /// <summary>
    /// Adds the new fields, writes the values (by full field name) with their appearance, and
    /// flattens the form if asked (values drawn into the pages, no field left).
    /// </summary>
    public static void Apply(PdfDocument document, IReadOnlyDictionary<string, string> values, IReadOnlyList<NewFormField> added, bool flatten)
    {
        if (IsDynamicXfa(document))
            throw new ToolException(Strings.Error_FormUnsupported);

        foreach (var field in added)
            Add(document, field);

        var form = AcroForm(document);
        // AcroForm fields win over XFA data once modified (as Acrobat does).
        form?.Elements.Remove("/XFA");

        foreach (var field in Terminals(document))
        {
            var name = FullName(field);
            var kind = Kind(field);
            if (values.TryGetValue(name, out var value))
                SetValue(document, field, kind, value);
            else
                RefreshAppearance(document, field, kind);
        }

        if (form is not null)
            form.Elements["/NeedAppearances"] = new PdfBoolean(!flatten);

        if (flatten)
            Flatten(document);
    }

    private static void SetValue(PdfDocument document, PdfDictionary field, FormFieldKind kind, string value)
    {
        switch (kind)
        {
            case FormFieldKind.Text or FormFieldKind.MultilineText or FormFieldKind.Choice:
                field.Elements["/V"] = Text(value);
                foreach (var widget in Widgets(field))
                    DrawText(document, field, widget, value, kind == FormFieldKind.MultilineText);
                break;

            case FormFieldKind.CheckBox:
                var on = value == Checked;
                foreach (var widget in Widgets(field))
                {
                    var state = EnsureButtonAppearance(document, widget, check: true);
                    widget.Elements["/AS"] = new PdfName(on ? state : "/Off");
                }
                field.Elements["/V"] = new PdfName(on ? EnsureButtonAppearance(document, Widgets(field).First(), check: true) : "/Off");
                break;

            case FormFieldKind.Radio:
                var chosen = "/" + value.TrimStart('/');
                foreach (var widget in Widgets(field))
                {
                    var state = EnsureButtonAppearance(document, widget, check: false);
                    widget.Elements["/AS"] = new PdfName(state == chosen ? state : "/Off");
                }
                field.Elements["/V"] = new PdfName(Widgets(field).Any(w => OnState(w) == value.TrimStart('/')) ? chosen : "/Off");
                break;
        }
    }

    /// <summary>Fields left untouched still get an appearance if they have none (for flattening).</summary>
    private static void RefreshAppearance(PdfDocument document, PdfDictionary field, FormFieldKind kind)
    {
        foreach (var widget in Widgets(field))
        {
            if (widget.Elements.ContainsKey("/AP"))
                continue;
            if (kind is FormFieldKind.Text or FormFieldKind.MultilineText or FormFieldKind.Choice)
                DrawText(document, field, widget, ValueText(field, kind) ?? "", kind == FormFieldKind.MultilineText);
            else if (kind is FormFieldKind.CheckBox or FormFieldKind.Radio)
                EnsureButtonAppearance(document, widget, kind == FormFieldKind.CheckBox);
        }
    }

    private static void Add(PdfDocument document, NewFormField added)
    {
        if (added.Page < 0 || added.Page >= document.PageCount)
            return;
        if (Terminals(document).Any(f => FullName(f) == added.Name))
            throw new ToolException(string.Format(Strings.Form_NameTaken, added.Name));

        var form = EnsureAcroForm(document);
        var page = document.Pages[added.Page];
        var field = new PdfDictionary(document);
        field.Elements["/Type"] = new PdfName("/Annot");
        field.Elements["/Subtype"] = new PdfName("/Widget");
        field.Elements["/T"] = Text(added.Name);
        field.Elements["/Rect"] = new PdfArray(document,
            new PdfReal(added.Rect.Left), new PdfReal(added.Rect.Bottom), new PdfReal(added.Rect.Right), new PdfReal(added.Rect.Top));
        field.Elements["/F"] = new PdfInteger(4); // printed
        field.Elements["/P"] = page.Reference;
        var characteristics = new PdfDictionary(document);
        characteristics.Elements["/BC"] = new PdfArray(document, new PdfReal(0.55), new PdfReal(0.55), new PdfReal(0.55));
        field.Elements["/MK"] = characteristics;

        var flags = added.Required ? FlagRequired : 0;
        if (added.Kind == FormFieldKind.CheckBox)
        {
            field.Elements["/FT"] = new PdfName("/Btn");
            field.Elements["/DA"] = new PdfString("/ZaDb 0 Tf 0 g");
            characteristics.Elements["/CA"] = new PdfString("4");
            field.Elements["/V"] = new PdfName("/Off");
            field.Elements["/AS"] = new PdfName("/Off");
        }
        else
        {
            field.Elements["/FT"] = new PdfName("/Tx");
            var size = added.FontSize > 0 ? added.FontSize.ToString("0.##", CultureInfo.InvariantCulture) : "0";
            field.Elements["/DA"] = new PdfString($"/Helv {size} Tf 0 g");
            if (added.Kind == FormFieldKind.MultilineText)
                flags |= FlagMultiline;
        }
        if (flags != 0)
            field.Elements["/Ff"] = new PdfInteger(flags);

        document.Internals.AddObject(field);
        ArrayOf(document, form, "/Fields").Elements.Add(field.Reference!);
        ArrayOf(document, page, "/Annots").Elements.Add(field.Reference!);

        var kind = added.Kind == FormFieldKind.CheckBox ? FormFieldKind.CheckBox : added.Kind;
        if (added.Value is { } value)
            SetValue(document, field, kind, value);
        else
            RefreshAppearance(document, field, kind);
    }

    // ---- Flattening -----------------------------------------------------------------------------

    /// <summary>Draws every visible widget's appearance into its page, then removes fields and widgets.</summary>
    public static void Flatten(PdfDocument document)
    {
        var counter = 0;
        foreach (var page in document.Pages)
        {
            if (page.Elements.GetArray("/Annots") is not { } annots)
                continue;

            var drawing = new StringBuilder();
            var kept = new List<PdfItem>();
            foreach (var item in annots.Elements)
            {
                if (Resolve(item) is not { } annotation || annotation.Elements.GetName("/Subtype") != "/Widget")
                {
                    kept.Add(item);
                    continue;
                }
                if ((annotation.Elements.GetInteger("/F") & AnnotationHidden) != 0 || NormalAppearance(annotation) is not { } appearance)
                    continue;

                EnsureFormXObject(appearance);
                var name = $"/UFFlat{++counter}";
                XObjects(document, page).Elements[name] = appearance.Reference ?? AddIndirect(document, appearance);
                drawing.Append(PlaceAppearance(annotation, appearance, name));
            }

            annots.Elements.Clear();
            foreach (var item in kept)
                annots.Elements.Add(item);
            if (annots.Elements.Count == 0)
                page.Elements.Remove("/Annots");

            if (drawing.Length > 0)
            {
                // Isolate the existing content so its graphics state can't move our drawing.
                page.Contents.PrependContent().CreateStream(Encoding.ASCII.GetBytes("q\n"));
                page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes("Q\n" + drawing));
            }
        }
        document.Internals.Catalog.Elements.Remove("/AcroForm");
    }

    /// <summary>The appearance maps its /BBox (after /Matrix) onto the widget's /Rect.</summary>
    private static string PlaceAppearance(PdfDictionary widget, PdfDictionary appearance, string name)
    {
        var rect = Rect(widget);
        var box = appearance.Elements.GetArray("/BBox") is { Elements.Count: 4 } b
            ? new PdfRect(Number(b.Elements[0]), Number(b.Elements[1]), Number(b.Elements[2]), Number(b.Elements[3]))
            : new PdfRect(0, 0, rect.Width, rect.Height);
        var scaleX = box.Width > 0 ? rect.Width / box.Width : 1;
        var scaleY = box.Height > 0 ? rect.Height / box.Height : 1;
        var tx = rect.Left - box.Left * scaleX;
        var ty = rect.Bottom - box.Bottom * scaleY;
        return string.Create(CultureInfo.InvariantCulture,
            $"q {scaleX:0.####} 0 0 {scaleY:0.####} {tx:0.###} {ty:0.###} cm {name} Do Q\n");
    }

    // ---- Appearances ----------------------------------------------------------------------------

    /// <summary>Text appearance: Helvetica, single line centered vertically, or wrapped from the top.</summary>
    private static void DrawText(PdfDocument document, PdfDictionary field, PdfDictionary widget, string value, bool multiline)
    {
        var rect = Rect(widget);
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        const double padding = 2;
        var size = FontSize(field);
        if (size <= 0)
            size = multiline ? 10 : Math.Clamp(rect.Height * 0.62, 6, 12);

        var content = new StringBuilder();
        content.Append("/Tx BMC\nq\n");
        content.Append(Invariant($"{padding / 2} {padding / 2} {rect.Width - padding} {rect.Height - padding} re W n\n"));
        content.Append(Invariant($"BT\n/Helv {size} Tf\n0 g\n"));

        var available = rect.Width - 2 * padding;
        if (multiline)
        {
            var leading = size * 1.15;
            var lines = Wrap(value, size, available);
            var y = rect.Height - padding - size;
            content.Append(Invariant($"{padding} {y} Td\n{leading} TL\n"));
            for (var i = 0; i < lines.Count; i++)
                content.Append(i == 0 ? $"{Literal(lines[i])} Tj\n" : $"{Literal(lines[i])} '\n");
        }
        else
        {
            var line = value.Replace('\r', ' ').Replace('\n', ' ');
            var y = (rect.Height - size * 0.72) / 2;
            content.Append(Invariant($"{padding} {y} Td\n{Literal(line)} Tj\n"));
        }
        content.Append("ET\nQ\nEMC\n");

        var stream = FormXObject(document, rect, content.ToString(), HelveticaResources(document));
        var appearances = new PdfDictionary(document);
        appearances.Elements["/N"] = stream.Reference;
        widget.Elements["/AP"] = appearances;
    }

    /// <summary>
    /// Makes sure a check box or radio button has an "on" and an "Off" appearance; returns the on-state
    /// name ("/Yes" when creating them). Existing appearances (Word, Acrobat) are kept as they are.
    /// </summary>
    private static string EnsureButtonAppearance(PdfDocument document, PdfDictionary widget, bool check)
    {
        if (OnState(widget) is { } existing)
            return "/" + existing;

        var rect = Rect(widget);
        var size = Math.Min(rect.Width, rect.Height) * 0.8;
        var symbol = check ? "4" : "l"; // ZapfDingbats: check mark, filled circle
        var border = Invariant($"0.55 G 1 w 0.5 0.5 {rect.Width - 1} {rect.Height - 1} re S\n");
        var mark = Invariant($"q BT /ZaDb {size} Tf 0 g {(rect.Width - size * 0.75) / 2} {(rect.Height - size * 0.7) / 2} Td ({symbol}) Tj ET Q\n");

        var resources = ZapfResources(document);
        var on = FormXObject(document, rect, border + mark, resources);
        var off = FormXObject(document, rect, border, resources);
        var states = new PdfDictionary(document);
        states.Elements["/Yes"] = on.Reference;
        states.Elements["/Off"] = off.Reference;
        var appearances = new PdfDictionary(document);
        appearances.Elements["/N"] = states;
        widget.Elements["/AP"] = appearances;
        return "/Yes";
    }

    private static PdfDictionary FormXObject(PdfDocument document, PdfRect rect, string content, PdfDictionary resources)
    {
        var stream = new PdfDictionary(document);
        stream.Elements["/Type"] = new PdfName("/XObject");
        stream.Elements["/Subtype"] = new PdfName("/Form");
        stream.Elements["/BBox"] = new PdfArray(document, new PdfReal(0), new PdfReal(0), new PdfReal(rect.Width), new PdfReal(rect.Height));
        stream.Elements["/Resources"] = resources;
        stream.CreateStream(Encoding.Latin1.GetBytes(content));
        document.Internals.AddObject(stream);
        return stream;
    }

    private static PdfDictionary HelveticaResources(PdfDocument document)
    {
        var fonts = new PdfDictionary(document);
        fonts.Elements["/Helv"] = StandardFont(document, "/Helvetica", winAnsi: true).Reference;
        var resources = new PdfDictionary(document);
        resources.Elements["/Font"] = fonts;
        return resources;
    }

    private static PdfDictionary ZapfResources(PdfDocument document)
    {
        var fonts = new PdfDictionary(document);
        fonts.Elements["/ZaDb"] = StandardFont(document, "/ZapfDingbats", winAnsi: false).Reference;
        var resources = new PdfDictionary(document);
        resources.Elements["/Font"] = fonts;
        return resources;
    }

    /// <summary>One shared dictionary per standard font and document.</summary>
    private static PdfDictionary StandardFont(PdfDocument document, string baseFont, bool winAnsi)
    {
        var form = EnsureAcroForm(document);
        var resources = form.Elements.GetDictionary("/DR") ?? new PdfDictionary(document);
        form.Elements["/DR"] = resources;
        var fonts = resources.Elements.GetDictionary("/Font") ?? new PdfDictionary(document);
        resources.Elements["/Font"] = fonts;
        var key = winAnsi ? "/Helv" : "/ZaDb";
        if (Resolve(fonts.Elements[key]) is { } existing && existing.Elements.GetName("/BaseFont") == baseFont)
            return existing;

        var font = new PdfDictionary(document);
        font.Elements["/Type"] = new PdfName("/Font");
        font.Elements["/Subtype"] = new PdfName("/Type1");
        font.Elements["/BaseFont"] = new PdfName(baseFont);
        if (winAnsi)
            font.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
        document.Internals.AddObject(font);
        fonts.Elements[key] = font.Reference;
        return font;
    }

    // ---- Text layout (Helvetica, WinAnsi) --------------------------------------------------------

    private static List<string> Wrap(string text, double size, double width)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && Helvetica.Width(candidate, size) > width)
                {
                    lines.Add(line);
                    line = word;
                }
                else
                {
                    line = candidate;
                }
            }
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>A PDF literal string in WinAnsi; characters outside it become "?" (the value itself keeps them).</summary>
    private static string Literal(string text)
    {
        var builder = new StringBuilder("(");
        foreach (var c in text)
        {
            var b = Helvetica.Encode(c);
            switch (b)
            {
                case (byte)'(' or (byte)')' or (byte)'\\':
                    builder.Append('\\').Append((char)b);
                    break;
                case < 32 or > 126:
                    builder.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                    break;
                default:
                    builder.Append((char)b);
                    break;
            }
        }
        return builder.Append(')').ToString();
    }

    // ---- Field tree -------------------------------------------------------------------------------

    private static PdfDictionary? AcroForm(PdfDocument document) =>
        Resolve(document.Internals.Catalog.Elements["/AcroForm"]);

    private static PdfDictionary EnsureAcroForm(PdfDocument document)
    {
        if (AcroForm(document) is { } existing)
            return existing;
        var form = new PdfDictionary(document);
        form.Elements["/Fields"] = new PdfArray(document);
        form.Elements["/DA"] = new PdfString("/Helv 0 Tf 0 g");
        document.Internals.AddObject(form);
        document.Internals.Catalog.Elements["/AcroForm"] = form.Reference;
        return form;
    }

    /// <summary>Fields holding a value (leaves of the field tree, merged with their widget or not).</summary>
    private static IEnumerable<PdfDictionary> Terminals(PdfDocument document)
    {
        if (AcroForm(document)?.Elements.GetArray("/Fields") is not { } roots)
            yield break;
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<PdfDictionary>(roots.Elements.Select(Resolve).OfType<PdfDictionary>().Reverse());
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!seen.Add(node))
                continue;
            var kids = node.Elements.GetArray("/Kids")?.Elements.Select(Resolve).OfType<PdfDictionary>().ToList() ?? [];
            // Kids with a name are sub-fields; kids without are this field's widgets.
            var subFields = kids.Where(k => k.Elements.ContainsKey("/T")).ToList();
            if (subFields.Count > 0)
            {
                for (var i = subFields.Count - 1; i >= 0; i--)
                    stack.Push(subFields[i]);
                continue;
            }
            if (Inherited(node, "/FT") is not null)
                yield return node;
        }
    }

    private static IEnumerable<PdfDictionary> Widgets(PdfDictionary field)
    {
        var kids = field.Elements.GetArray("/Kids")?.Elements.Select(Resolve).OfType<PdfDictionary>().Where(k => !k.Elements.ContainsKey("/T")).ToList();
        return kids is { Count: > 0 } ? kids : [field];
    }

    private static string FullName(PdfDictionary field)
    {
        var parts = new List<string>();
        for (var node = field; node is not null; node = Resolve(node.Elements["/Parent"]))
        {
            if (node.Elements.ContainsKey("/T"))
                parts.Add(node.Elements.GetString("/T"));
        }
        parts.Reverse();
        return string.Join(".", parts);
    }

    private static PdfItem? Inherited(PdfDictionary field, string key)
    {
        for (var node = field; node is not null; node = Resolve(node.Elements["/Parent"]))
        {
            if (node.Elements[key] is { } value)
                return value;
        }
        return null;
    }

    private static int Flags(PdfDictionary field) => Inherited(field, "/Ff") is PdfInteger i ? i.Value : 0;

    private static FormFieldKind Kind(PdfDictionary field)
    {
        var flags = Flags(field);
        return (Inherited(field, "/FT") as PdfName)?.Value switch
        {
            "/Tx" => (flags & FlagMultiline) != 0 ? FormFieldKind.MultilineText : FormFieldKind.Text,
            "/Btn" when (flags & FlagPushButton) != 0 => FormFieldKind.Unsupported,
            "/Btn" => (flags & FlagRadio) != 0 ? FormFieldKind.Radio : FormFieldKind.CheckBox,
            "/Ch" => FormFieldKind.Choice,
            "/Sig" => FormFieldKind.Signature,
            _ => FormFieldKind.Unsupported,
        };
    }

    private static string? ValueText(PdfDictionary field, FormFieldKind kind) => Inherited(field, "/V") switch
    {
        PdfString s => s.Value,
        PdfName n when kind == FormFieldKind.CheckBox => n.Value == "/Off" ? Unchecked : Checked,
        PdfName n => n.Value == "/Off" ? null : n.Value.TrimStart('/'),
        PdfArray a => string.Join(", ", a.Elements.OfType<PdfString>().Select(s => s.Value)),
        _ => kind == FormFieldKind.CheckBox ? Unchecked : null,
    };

    private static List<string> ChoiceOptions(PdfDictionary field) =>
        ResolveArray(Inherited(field, "/Opt"))?.Elements
            .Select(item => item switch
            {
                PdfString s => s.Value,
                PdfArray pair when pair.Elements.Count == 2 && pair.Elements[1] is PdfString label => label.Value,
                _ => null,
            })
            .OfType<string>()
            .ToList() ?? [];

    /// <summary>Name of a button widget's "on" appearance (without the slash), if it has one.</summary>
    private static string? OnState(PdfDictionary widget) =>
        Resolve(Resolve(widget.Elements["/AP"])?.Elements["/N"]) is { } states && states.Stream is null
            ? states.Elements.Keys.FirstOrDefault(k => k != "/Off")?.TrimStart('/')
            : null;

    /// <summary>The appearance stream drawn now: /N, or for buttons the state named by /AS.</summary>
    private static PdfDictionary? NormalAppearance(PdfDictionary widget)
    {
        var normal = Resolve(Resolve(widget.Elements["/AP"])?.Elements["/N"]);
        if (normal is null)
            return null;
        if (normal.Stream is not null)
            return normal;
        var state = widget.Elements.GetName("/AS");
        return string.IsNullOrEmpty(state) ? null : Resolve(normal.Elements[state]);
    }

    private static double FontSize(PdfDictionary field)
    {
        var appearance = (Inherited(field, "/DA") as PdfString)?.Value ?? "";
        var parts = appearance.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var index = System.Array.IndexOf(parts, "Tf");
        return index >= 1 && double.TryParse(parts[index - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var size) ? size : 0;
    }

    private static PdfRect Rect(PdfDictionary widget)
    {
        if (widget.Elements.GetArray("/Rect") is not { Elements.Count: 4 } r)
            return default;
        double a = Number(r.Elements[0]), b = Number(r.Elements[1]), c = Number(r.Elements[2]), d = Number(r.Elements[3]);
        return new PdfRect(Math.Min(a, c), Math.Min(b, d), Math.Max(a, c), Math.Max(b, d));
    }

    private static int PageIndex(PdfDocument document, PdfDictionary widget)
    {
        for (var i = 0; i < document.PageCount; i++)
        {
            if (document.Pages[i].Elements.GetArray("/Annots") is { } annots && annots.Elements.Any(a => ReferenceEquals(Resolve(a), widget)))
                return i;
        }
        return Resolve(widget.Elements["/P"]) is { } page
            ? Enumerable.Range(0, document.PageCount).FirstOrDefault(i => ReferenceEquals(document.Pages[i], page))
            : 0;
    }

    // ---- PDF object helpers -------------------------------------------------------------------

    private static PdfDictionary? Resolve(PdfItem? item) => item switch
    {
        PdfReference reference => reference.Value as PdfDictionary,
        PdfDictionary dictionary => dictionary,
        _ => null,
    };

    private static double Number(PdfItem item) => item switch
    {
        PdfInteger i => i.Value,
        PdfReal r => r.Value,
        PdfReference { Value: PdfIntegerObject i } => i.Value,
        PdfReference { Value: PdfRealObject r } => r.Value,
        _ => 0,
    };

    private static PdfArray? ResolveArray(PdfItem? item) => item switch
    {
        PdfReference { Value: PdfArray array } => array,
        PdfArray array => array,
        _ => null,
    };

    private static PdfArray ArrayOf(PdfDocument document, PdfDictionary owner, string key)
    {
        if (owner.Elements[key] is PdfReference { Value: PdfArray indirect })
            return indirect;
        if (owner.Elements[key] is PdfArray direct)
            return direct;
        var created = new PdfArray(document);
        owner.Elements[key] = created;
        return created;
    }

    private static PdfDictionary XObjects(PdfDocument document, PdfPage page)
    {
        var resources = Resolve(page.Elements["/Resources"]) ?? new PdfDictionary(document);
        page.Elements["/Resources"] = resources.Reference is { } r ? r : resources;
        var xobjects = Resolve(resources.Elements["/XObject"]) ?? new PdfDictionary(document);
        resources.Elements["/XObject"] = xobjects.Reference is { } x ? x : xobjects;
        return xobjects;
    }

    private static void EnsureFormXObject(PdfDictionary appearance)
    {
        appearance.Elements["/Type"] = new PdfName("/XObject");
        appearance.Elements["/Subtype"] = new PdfName("/Form");
    }

    private static PdfReference AddIndirect(PdfDocument document, PdfDictionary dictionary)
    {
        document.Internals.AddObject(dictionary);
        return dictionary.Reference!;
    }

    private static PdfString Text(string value) =>
        value.All(c => c < 128) ? new PdfString(value) : new PdfString(value, PdfStringEncoding.Unicode);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Helvetica metrics (Adobe AFM) for the WinAnsi characters, to wrap field text.</summary>
internal static class Helvetica
{
    // Widths in thousandths of an em for bytes 32–255 (WinAnsiEncoding).
    private static readonly short[] Widths =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, // 32
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556, // 48
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, // 64
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556, // 80
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, // 96
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584, 350, // 112
        556, 350, 222, 556, 333, 1000, 556, 556, 333, 1000, 667, 333, 1000, 350, 611, 350, // 128
        350, 222, 222, 333, 333, 350, 556, 1000, 333, 1000, 500, 333, 944, 350, 500, 667, // 144
        278, 333, 556, 556, 556, 556, 260, 556, 333, 737, 370, 556, 584, 333, 737, 333, // 160
        400, 584, 333, 333, 333, 556, 537, 278, 333, 333, 365, 556, 834, 834, 834, 611, // 176
        667, 667, 667, 667, 667, 667, 1000, 722, 667, 667, 667, 667, 278, 278, 278, 278, // 192
        722, 722, 778, 778, 778, 778, 778, 584, 778, 722, 722, 722, 722, 667, 667, 611, // 208
        556, 556, 556, 556, 556, 556, 889, 500, 556, 556, 556, 556, 278, 278, 278, 278, // 224
        556, 556, 556, 556, 556, 556, 556, 584, 611, 556, 556, 556, 556, 500, 556, 500, // 240
    ];

    // WinAnsi 0x80–0x9F: the Unicode characters they stand for.
    private static readonly Dictionary<char, byte> Extra = new()
    {
        ['€'] = 0x80, ['‚'] = 0x82, ['ƒ'] = 0x83, ['„'] = 0x84, ['…'] = 0x85, ['†'] = 0x86, ['‡'] = 0x87,
        ['ˆ'] = 0x88, ['‰'] = 0x89, ['Š'] = 0x8A, ['‹'] = 0x8B, ['Œ'] = 0x8C, ['Ž'] = 0x8E, ['‘'] = 0x91,
        ['’'] = 0x92, ['“'] = 0x93, ['”'] = 0x94, ['•'] = 0x95, ['–'] = 0x96, ['—'] = 0x97, ['˜'] = 0x98,
        ['™'] = 0x99, ['š'] = 0x9A, ['›'] = 0x9B, ['œ'] = 0x9C, ['ž'] = 0x9E, ['Ÿ'] = 0x9F,
    };

    public static byte Encode(char c) => c switch
    {
        >= ' ' and <= '~' => (byte)c,
        >= ' ' and <= 'ÿ' => (byte)c,
        _ when Extra.TryGetValue(c, out var b) => b,
        _ => (byte)'?',
    };

    public static double Width(string text, double size) => text.Sum(c => Widths[Encode(c) - 32]) * size / 1000;
}
