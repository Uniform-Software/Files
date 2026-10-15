using System.Globalization;
using System.Text;
using System.Text.Json;
using UniFiles.Core.Resources;

namespace UniFiles.Core.Tools.Pdf;

/// <summary>
/// Fills a form, adds simple fields and can flatten it. Set up in the form editor, which stores
/// "values" (JSON: full field name → value), "added" (JSON list of <see cref="NewFormField"/>) and
/// "flatten" (true to freeze the answers into the pages).
/// </summary>
public sealed class PdfFormFillTool : PdfToolBase
{
    public const string ValuesKey = "values";
    public const string AddedKey = "added";
    public const string FlattenKey = "flatten";

    public override string Id => "pdf.formFill";
    public override string Name => Strings.Tool_PdfFormFill_Name;
    public override string Description => Strings.Tool_PdfFormFill_Description;
    public override int MaximumInputs => 1;
    public override bool NeedsEditor => true;
    public override bool ShowsSizeChange => false;

    public override IReadOnlyList<ToolOption> Options { get; } = [];

    public static string SerializeValues(IReadOnlyDictionary<string, string> values) => JsonSerializer.Serialize(values);

    public static string SerializeAdded(IReadOnlyList<NewFormField> added) => JsonSerializer.Serialize(added);

    public override async Task<IReadOnlyList<string>> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var input = context.Inputs[0];
        var values = Deserialize<Dictionary<string, string>>(context.Settings.GetString(ValuesKey)) ?? [];
        var added = Deserialize<List<NewFormField>>(context.Settings.GetString(AddedKey)) ?? [];
        var flatten = context.Settings.GetBool(FlattenKey);
        var suffix = flatten ? Strings.Suffix_Flattened : Strings.Suffix_Filled;

        var output = await WriteOutputAsync(context.Output, OutputName(input, ".pdf", " - " + suffix), path => Task.Run(() =>
        {
            using var document = PdfForms.Open(input);
            if (PdfForms.IsSigned(document))
                throw new ToolException(Strings.Error_ModifySigned);
            PdfForms.Apply(document, values, added, flatten);
            Save(document, path);
        }, cancellationToken));
        context.Progress.Report(1);
        return [output];
    }

    private static T? Deserialize<T>(string json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Quick action: freezes the answers of filled forms into the pages (nothing editable left).</summary>
public sealed class PdfFormFlattenTool : PdfToolBase
{
    public override string Id => "pdf.formFlatten";
    public override string Name => Strings.Tool_PdfFormFlatten_Name;
    public override string Description => Strings.Tool_PdfFormFlatten_Description;
    public override bool ShowsSizeChange => false;

    public override IReadOnlyList<ToolOption> Options { get; } = [];

    public override async Task<IReadOnlyList<string>> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var input = context.Inputs[0];
        var output = await WriteOutputAsync(context.Output, OutputName(input, ".pdf", " - " + Strings.Suffix_Flattened), path => Task.Run(() =>
        {
            using var document = PdfForms.Open(input);
            if (PdfForms.IsSigned(document))
                throw new ToolException(Strings.Error_ModifySigned);
            if (PdfForms.Read(document).Count == 0)
                throw new ToolException(Strings.Error_NoFormFields);
            PdfForms.Apply(document, new Dictionary<string, string>(), [], flatten: true);
            Save(document, path);
        }, cancellationToken));
        context.Progress.Report(1);
        return [output];
    }
}

/// <summary>
/// The answers of several filled forms in one table: one row per PDF, one column per field (all
/// the fields of all the files), the file name first. UTF-8 with a byte order mark, so Excel reads
/// the accents; ";" as separator by default in French (what Excel expects there), "," otherwise.
/// </summary>
public sealed class PdfFormExportTool : PdfToolBase
{
    public override string Id => "pdf.formExport";
    public override string Name => Strings.Tool_PdfFormExport_Name;
    public override string Description => Strings.Tool_PdfFormExport_Description;
    public override bool CombinesInputs => true;
    public override int MinimumInputs => 1;
    public override bool ShowsSizeChange => false;
    public override FileKind OutputKind(FileKind input) => FileKind.Unknown;

    public override IReadOnlyList<ToolOption> Options { get; } =
    [
        new ChoiceOption("separator", Strings.Option_Separator,
        [
            new Choice("semicolon", Strings.Choice_Semicolon),
            new Choice("comma", Strings.Choice_Comma),
        ], CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr" ? "semicolon" : "comma") { Hint = Strings.Hint_Separator },
        new ChoiceOption("checkboxes", Strings.Option_CheckboxValues,
        [
            new Choice("words", Strings.Choice_YesNo),
            new Choice("digits", "1 / 0"),
        ], "words"),
    ];

    public override async Task<IReadOnlyList<string>> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var separator = context.Settings.GetString("separator", "semicolon") == "comma" ? ',' : ';';
        var digits = context.Settings.GetString("checkboxes", "words") == "digits";

        var rows = new List<(string File, Dictionary<string, string> Values)>();
        var columns = new List<string>();
        for (var i = 0; i < context.Inputs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = context.Inputs[i];
            var values = new Dictionary<string, string>();
            foreach (var field in await Task.Run(() => PdfForms.Read(input), cancellationToken))
            {
                if (field.Kind is FormFieldKind.Signature or FormFieldKind.Unsupported)
                    continue;
                if (!columns.Contains(field.Name))
                    columns.Add(field.Name);
                values[field.Name] = field.Kind == FormFieldKind.CheckBox
                    ? (field.Value == PdfForms.Checked
                        ? digits ? "1" : Strings.Choice_Yes
                        : digits ? "0" : Strings.Choice_No)
                    : field.Value ?? "";
            }
            rows.Add((Path.GetFileName(input), values));
            context.Progress.Report((i + 1.0) / (context.Inputs.Count + 1));
        }
        if (columns.Count == 0)
            throw new ToolException(Strings.Error_NoFormFields);

        var csv = new StringBuilder();
        csv.AppendLine(string.Join(separator, new[] { Strings.Csv_File }.Concat(columns).Select(c => Quote(c, separator))));
        foreach (var (file, values) in rows)
            csv.AppendLine(string.Join(separator, new[] { file }.Concat(columns.Select(c => values.GetValueOrDefault(c, ""))).Select(v => Quote(v, separator))));

        var name = $"{Strings.Csv_Answers} {DateTime.Now:yyyy-MM-dd}.csv";
        var output = await WriteOutputAsync(context.Output, name, path =>
            File.WriteAllTextAsync(path, csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken));
        context.Progress.Report(1);
        return [output];
    }

    /// <summary>RFC 4180: quotes around values holding the separator, quotes or line breaks.</summary>
    internal static string Quote(string value, char separator) =>
        value.IndexOfAny([separator, '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}
