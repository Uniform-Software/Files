using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UniFiles.Core.Resources;
using UniFiles.Core.Tools;

namespace UniFiles.Core.Signing;

/// <summary>
/// Changes a PDF by adding to its end, without touching a byte of it, as signatures require: the
/// document is opened with PDFsharp, changed in memory, then only the new and changed objects are
/// written after the original, with a cross-reference section of the same kind as the original's
/// (table, or stream for PDF 1.5+ files) pointing back to it (/Prev). Object numbers are those of
/// the file (see <see cref="Open"/>).
/// </summary>
public sealed partial class IncrementalUpdate : IDisposable
{
    private readonly byte[] _original;
    private readonly Dictionary<int, byte[]> _snapshot = [];
    private readonly Dictionary<string, string> _placeholders = [];

    private IncrementalUpdate(byte[] original, PdfDocument document)
    {
        _original = original;
        Document = document;
        foreach (var item in document.Internals.GetAllObjects())
            _snapshot[item.Reference!.ObjectNumber] = Hash(item);
    }

    public PdfDocument Document { get; }

    /// <summary>Opens a PDF for an incremental update. Encrypted and unreadable files are refused.</summary>
    public static IncrementalUpdate Open(byte[] original)
    {
        if (Trailer(original).Contains("/Encrypt", StringComparison.Ordinal))
            throw new ToolException(Strings.Error_PdfProtected);
        PdfDocument document;
        try
        {
            // Import, not Modify: Modify compacts and renumbers the objects on reading, and the update
            // must refer to the numbers of the file. The document is changed in memory and written
            // by this class, never saved by PDFsharp.
            document = PdfReader.Open(new MemoryStream(original), PdfDocumentOpenMode.Import);
        }
        catch (Exception ex) when (ex is PdfReaderException or InvalidOperationException && ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException(Strings.Error_PdfProtected, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ToolException(Strings.Error_PdfUnreadable, ex);
        }
        return new IncrementalUpdate(original, document);
    }

    /// <summary>
    /// A value written as is and found again after writing (signature /ByteRange and /Contents):
    /// returns the literal to put in the object.
    /// </summary>
    public PdfLiteral Placeholder(string key, string text)
    {
        _placeholders[key] = text;
        return new PdfLiteral(text);
    }

    /// <summary>The whole file: original bytes, then the update. <paramref name="positions"/>: where each placeholder starts.</summary>
    public byte[] Write(out IReadOnlyDictionary<string, long> positions)
    {
        var output = new MemoryStream();
        output.Write(_original);
        if (_original.Length > 0 && _original[^1] is not (byte)'\n' and not (byte)'\r')
            output.WriteByte((byte)'\n');

        var offsets = new SortedDictionary<int, (long Offset, int Generation)>();
        var maxNumber = 0;
        foreach (var item in Document.Internals.GetAllObjects())
        {
            var reference = item.Reference!;
            maxNumber = Math.Max(maxNumber, reference.ObjectNumber);
            if (_snapshot.TryGetValue(reference.ObjectNumber, out var before) && before.AsSpan().SequenceEqual(Hash(item)))
                continue;
            offsets[reference.ObjectNumber] = (output.Position, reference.GenerationNumber);
            Document.Internals.WriteObject(output, item);
        }

        var previous = StartXref(_original);
        var trailer = TrailerEntries(maxNumber, previous);
        if (IsStreamXref(_original, previous))
            WriteXrefStream(output, offsets, maxNumber + 1, trailer);
        else
            WriteXrefTable(output, offsets, maxNumber + 1, trailer);

        var bytes = output.ToArray();
        var found = new Dictionary<string, long>();
        var text = Encoding.Latin1.GetString(bytes, _original.Length, bytes.Length - _original.Length);
        foreach (var (key, value) in _placeholders)
        {
            var at = text.IndexOf(value, StringComparison.Ordinal);
            if (at < 0)
                throw new InvalidOperationException($"Placeholder {key} not written.");
            found[key] = _original.Length + Encoding.Latin1.GetByteCount(text.AsSpan(0, at));
        }
        positions = found;
        return bytes;
    }

    public void Dispose() => Document.Dispose();

    // ---- Cross-reference sections ------------------------------------------------------------------

    private string TrailerEntries(int maxNumber, long previous)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"/Root {Document.Internals.Catalog.Reference!.ObjectNumber} {Document.Internals.Catalog.Reference.GenerationNumber} R");
        if (Document.Info.Reference is { } info)
            builder.Append(CultureInfo.InvariantCulture, $"/Info {info.ObjectNumber} {info.GenerationNumber} R");
        var first = Hex(Document.Internals.FirstDocumentID);
        var second = Hex(Document.Internals.SecondDocumentID);
        if (first.Length > 0)
            builder.Append(CultureInfo.InvariantCulture, $"/ID[<{first}><{(second.Length > 0 ? second : first)}>]");
        builder.Append(CultureInfo.InvariantCulture, $"/Prev {previous}");
        return builder.ToString();

        static string Hex(string raw) => Convert.ToHexString(Encoding.Latin1.GetBytes(raw ?? ""));
    }

    private static void WriteXrefTable(Stream output, SortedDictionary<int, (long Offset, int Generation)> offsets, int size, string trailer)
    {
        var start = output.Position;
        var text = new StringBuilder("xref\n");
        foreach (var run in Runs(offsets.Keys))
        {
            text.Append(CultureInfo.InvariantCulture, $"{run.First} {run.Count}\n");
            for (var n = run.First; n < run.First + run.Count; n++)
                text.Append(CultureInfo.InvariantCulture, $"{offsets[n].Offset:D10} {offsets[n].Generation:D5} n\r\n");
        }
        text.Append(CultureInfo.InvariantCulture, $"trailer\n<</Size {size}{trailer}>>\nstartxref\n{start}\n%%EOF\n");
        output.Write(Encoding.Latin1.GetBytes(text.ToString()));
    }

    private static void WriteXrefStream(Stream output, SortedDictionary<int, (long Offset, int Generation)> offsets, int number, string trailer)
    {
        var start = output.Position;
        offsets[number] = (start, 0);
        var entries = new MemoryStream();
        foreach (var (_, (offset, generation)) in offsets)
        {
            entries.WriteByte(1);
            entries.Write([(byte)(offset >> 24), (byte)(offset >> 16), (byte)(offset >> 8), (byte)offset]);
            entries.Write([(byte)(generation >> 8), (byte)generation]);
        }
        var index = string.Join(" ", Runs(offsets.Keys).Select(r => $"{r.First} {r.Count}"));
        var head = $"{number} 0 obj\n<</Type/XRef/Size {number + 1}/W[1 4 2]/Index[{index}]/Length {entries.Length}{trailer}>>\nstream\n";
        output.Write(Encoding.Latin1.GetBytes(head));
        output.Write(entries.ToArray());
        output.Write(Encoding.Latin1.GetBytes(string.Create(CultureInfo.InvariantCulture, $"\nendstream\nendobj\nstartxref\n{start}\n%%EOF\n")));
    }

    /// <summary>Consecutive object numbers grouped into subsections.</summary>
    private static IEnumerable<(int First, int Count)> Runs(IEnumerable<int> numbers)
    {
        int? first = null;
        var count = 0;
        foreach (var n in numbers)
        {
            if (first is { } f && n == f + count)
            {
                count++;
                continue;
            }
            if (first is { } done)
                yield return (done, count);
            first = n;
            count = 1;
        }
        if (first is { } last)
            yield return (last, count);
    }

    // ---- Reading the original's end ------------------------------------------------------------------

    internal static long StartXref(byte[] pdf)
    {
        var tail = Encoding.Latin1.GetString(pdf, Math.Max(0, pdf.Length - 2048), Math.Min(2048, pdf.Length));
        var matches = StartXrefPattern().Matches(tail);
        if (matches.Count == 0)
            throw new ToolException(Strings.Error_PdfUnreadable);
        return long.Parse(matches[^1].Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static bool IsStreamXref(byte[] pdf, long offset)
    {
        var at = (int)offset;
        while (at < pdf.Length && pdf[at] is (byte)' ' or (byte)'\r' or (byte)'\n')
            at++;
        return !(at + 4 <= pdf.Length && Encoding.Latin1.GetString(pdf, at, 4) == "xref");
    }

    /// <summary>The last trailer dictionary (classic trailer, or the xref stream's dictionary), as text.</summary>
    private static string Trailer(byte[] pdf)
    {
        try
        {
            var offset = (int)StartXref(pdf);
            var length = Math.Min(pdf.Length - offset, 1 << 20);
            var text = Encoding.Latin1.GetString(pdf, offset, length);
            var start = text.IndexOf("trailer", StringComparison.Ordinal) is var t and >= 0 ? t : 0;
            var end = text.IndexOf("stream", start, StringComparison.Ordinal) is var s and >= 0 ? s : text.Length;
            var trailerEnd = text.IndexOf("startxref", start, StringComparison.Ordinal) is var x and >= 0 ? x : text.Length;
            return text[start..Math.Min(end, trailerEnd)];
        }
        catch (ToolException)
        {
            return "";
        }
    }

    private byte[] Hash(PdfObject item)
    {
        using var buffer = new MemoryStream();
        Document.Internals.WriteObject(buffer, item);
        return SHA256.HashData(buffer.ToArray());
    }

    [GeneratedRegex(@"startxref\s+(\d+)")]
    private static partial Regex StartXrefPattern();
}
