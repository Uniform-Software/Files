using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UniFiles.Core.Resources;
using UniFiles.Core.Signing;

namespace UniFiles.Core.Tools.Pdf;

/// <summary>
/// Signs a PDF with a certificate (PAdES). Set up in the signing screen, which stores: "identity"
/// (certificate id), "placement" ("invisible", "field:name" or "box:page:left,bottom,right,top"),
/// "reason", "location", what the visible signature shows, and the time-stamping service if any.
/// </summary>
public sealed class PdfSignTool : PdfToolBase
{
    public const string IdentityKey = "identity";
    public const string PlacementKey = "placement";
    public const string ReasonKey = "reason";
    public const string LocationKey = "location";
    public const string ShowNameKey = "showName";
    public const string ShowDateKey = "showDate";
    public const string ShowReasonKey = "showReason";
    public const string HandwritingKey = "handwriting"; // none, drawn, image
    public const string TimestampKey = "timestamp";     // service address, empty for none
    public const string FlattenKey = "flatten";         // freeze the form's answers first

    public override string Id => "pdf.sign";
    public override ToolModule Module => ToolModule.Signature;
    public override string Name => Strings.Tool_PdfSign_Name;
    public override string Description => Strings.Tool_PdfSign_Description;
    public override int MaximumInputs => 1;
    public override bool NeedsEditor => true;
    public override bool ShowsSizeChange => false;

    public override IReadOnlyList<ToolOption> Options { get; } = [];

    public static string Placement(SignaturePlacement placement) => placement switch
    {
        SignaturePlacement.ExistingField field => "field:" + field.Name,
        SignaturePlacement.NewBox box => string.Create(CultureInfo.InvariantCulture,
            $"box:{box.Page}:{box.Rect.Left:0.##},{box.Rect.Bottom:0.##},{box.Rect.Right:0.##},{box.Rect.Top:0.##}"),
        _ => "invisible",
    };

    public static SignaturePlacement ParsePlacement(string text)
    {
        if (text.StartsWith("field:", StringComparison.Ordinal))
            return new SignaturePlacement.ExistingField(text[6..]);
        var parts = text.Split(':');
        if (parts is ["box", var page, var box] && box.Split(',') is { Length: 4 } numbers)
        {
            var v = numbers.Select(n => double.Parse(n, CultureInfo.InvariantCulture)).ToArray();
            return new SignaturePlacement.NewBox(int.Parse(page, CultureInfo.InvariantCulture), new PdfRect(v[0], v[1], v[2], v[3]));
        }
        return new SignaturePlacement.Invisible();
    }

    /// <summary>The form's answers frozen into the pages (signature fields kept), before signing.</summary>
    private static byte[] Flattened(string input)
    {
        using var document = PdfForms.Open(input);
        if (PdfForms.IsSigned(document))
            throw new ToolException(Strings.Error_ModifySigned);
        PdfForms.Flatten(document);
        var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    public override async Task<IReadOnlyList<string>> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var input = context.Inputs[0];
        var settings = context.Settings;
        var identity = SigningContext.Identities.Find(settings.GetString(IdentityKey))
            ?? throw new ToolException(Strings.Sign_IdentityMissing);
        if (!identity.IsValidNow)
            throw new ToolException(string.Format(CultureInfo.CurrentCulture, Strings.Error_CertificateExpired, identity.NotAfter));

        var time = DateTimeOffset.Now;
        var reason = settings.GetString(ReasonKey);
        var lines = new List<string>();
        if (settings.GetBool(ShowNameKey, true))
            lines.Add(identity.Name);
        if (settings.GetBool(ShowDateKey, true))
            lines.Add(time.LocalDateTime.ToString("g", CultureInfo.CurrentCulture));
        if (settings.GetBool(ShowReasonKey, true) && !string.IsNullOrWhiteSpace(reason))
            lines.Add(reason);
        var handwriting = settings.GetString(HandwritingKey, "none");
        var timestamp = settings.GetString(TimestampKey);

        var request = new SignatureRequest
        {
            Certificate = identity.Certificate,
            Chain = SigningContext.Identities.ChainOf(identity),
            Placement = ParsePlacement(settings.GetString(PlacementKey, "invisible")),
            Reason = reason,
            Location = settings.GetString(LocationKey),
            SignerName = identity.Name,
            SigningTime = time,
            AppearanceLines = lines,
            Drawing = handwriting == "drawn" ? SigningContext.Handwriting.Drawing(identity.Id) : null,
            HandwritingImage = handwriting == "image" ? SigningContext.Handwriting.Image(identity.Id) : null,
            Timestamp = Uri.TryCreate(timestamp, UriKind.Absolute, out var service) ? new Rfc3161Timestamper(service).Provider : null,
        };

        var original = await File.ReadAllBytesAsync(input, cancellationToken);
        if (settings.GetBool(FlattenKey))
            original = await Task.Run(() => Flattened(input), cancellationToken);
        byte[] signed;
        try
        {
            signed = await PdfSigner.SignAsync(original, request, cancellationToken);
        }
        catch (CryptographicException ex)
        {
            // The key said no: PIN not entered, card removed, access refused in the keychain.
            throw new ToolException(Strings.Sign_KeyRefused, ex);
        }
        catch (ArgumentException ex)
        {
            throw new ToolException(Strings.Sign_FieldMissing, ex);
        }

        var output = await WriteOutputAsync(context.Output, OutputName(input, ".pdf", " - " + Strings.Suffix_Signed),
            path => File.WriteAllBytesAsync(path, signed, cancellationToken));
        context.Progress.Report(1);
        return [output];
    }
}

/// <summary>
/// Checks the signatures of PDFs. In the app the report opens in a window; run as an action
/// (right-click), it writes the report as a text file.
/// </summary>
public sealed class PdfVerifyTool : PdfToolBase
{
    public override string Id => "pdf.verify";
    public override ToolModule Module => ToolModule.Signature;
    public override string Name => Strings.Tool_PdfVerify_Name;
    public override string Description => Strings.Tool_PdfVerify_Description;
    public override bool CombinesInputs => true;
    public override int MinimumInputs => 1;
    public override bool NeedsEditor => true;
    public override bool ShowsSizeChange => false;
    public override FileKind OutputKind(FileKind input) => FileKind.Unknown;

    public override IReadOnlyList<ToolOption> Options { get; } = [];

    /// <summary>The report of every file, in Markdown (headings, lists).</summary>
    public static string Report(IEnumerable<string> files)
    {
        var trusted = SigningContext.Identities.TrustedAuthorities();
        var text = new StringBuilder();
        foreach (var file in files)
        {
            if (text.Length > 0)
                text.Append("\n---\n\n");
            try
            {
                text.Append(SignatureReportText.Markdown(Path.GetFileName(file), PdfSignatureVerifier.Verify(File.ReadAllBytes(file), trusted)));
            }
            catch (ToolException ex)
            {
                text.Append(CultureInfo.CurrentCulture, $"# {Path.GetFileName(file)}\n\n{ex.Message}\n");
            }
        }
        text.Append('\n').Append(Strings.Verify_Offline).Append('\n');
        return text.ToString();
    }

    public override async Task<IReadOnlyList<string>> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var report = await Task.Run(() => Report(context.Inputs), cancellationToken);
        var name = $"{Strings.Verify_FileName} {DateTime.Now:yyyy-MM-dd}.txt";
        var output = await WriteOutputAsync(context.Output, name, path => File.WriteAllTextAsync(path, report, Encoding.UTF8, cancellationToken));
        context.Progress.Report(1);
        return [output];
    }
}
