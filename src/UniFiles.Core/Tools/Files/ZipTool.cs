using System.IO.Compression;
using UniFiles.Core.Resources;

namespace UniFiles.Core.Tools.Files;

/// <summary>
/// One ZIP archive of every dropped file. Files from a dropped folder keep their sub-folders in
/// the archive. Entry names are UTF-8, so accents read right on macOS and Windows.
/// </summary>
public sealed class ZipTool : FileToolBase
{
    private static readonly IReadOnlySet<FileKind> AnyKind = new HashSet<FileKind>();

    public override string Id => "files.zip";
    public override ToolModule Module => ToolModule.Files;
    public override string Name => Strings.Tool_Zip_Name;
    public override string Description => Strings.Tool_Zip_Description;
    public override IReadOnlySet<FileKind> AcceptedKinds => AnyKind;
    public override bool CombinesInputs => true;
    public override int MinimumInputs => 1;
    public override FileKind OutputKind(FileKind input) => FileKind.Unknown;

    public override IReadOnlyList<ToolOption> Options { get; } =
    [
        new ChoiceOption("level", Strings.Option_Compression,
        [
            new Choice("fast", Strings.Choice_ZipFast),
            new Choice("balanced", Strings.Choice_ZipBalanced),
            new Choice("smallest", Strings.Choice_ZipSmallest),
        ], "balanced"),
    ];

    // Photos and videos are already compressed: a rough guess for the estimate.
    public override long? EstimateOutputSize(IReadOnlyList<string> inputs, ToolSettings settings) =>
        inputs.Where(File.Exists).Sum(i => FileKindDetector.Detect(i) is FileKind.Unknown or FileKind.Pdf
            ? new FileInfo(i).Length / 2
            : new FileInfo(i).Length);

    public override async Task<IReadOnlyList<string>> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var files = context.Files;
        var level = context.Settings.GetString("level", "balanced") switch
        {
            "fast" => CompressionLevel.Fastest,
            "smallest" => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal,
        };
        // Named after the dropped folder when there is a single one.
        var roots = files.Select(f => f.RootName).Distinct().ToList();
        var name = roots is [{ } root] ? root : $"UniFiles {DateTime.Now:yyyy-MM-dd}";

        var output = await WriteOutputAsync(context.Output, name + ".zip", path => Task.Run(() =>
        {
            // The output folder reserved the name with an empty file: write over it.
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, System.Text.Encoding.UTF8);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                archive.CreateEntryFromFile(files[i].Path, EntryName(files[i], used), level);
                context.Progress.Report((i + 1.0) / files.Count);
            }
        }, cancellationToken));
        return [output];
    }

    /// <summary>"Plage/IMG_1.jpg" for a file from a dropped folder (inside it), else its name; never twice the same.</summary>
    internal static string EntryName(InputFile file, ISet<string> used)
    {
        var folder = file.RootName is null ? "" : file.RelativeDirectory.Replace('\\', '/');
        var name = Path.GetFileName(file.Path);
        var entry = folder.Length == 0 ? name : $"{folder}/{name}";
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var n = 2; !used.Add(entry); n++)
            entry = (folder.Length == 0 ? "" : folder + "/") + $"{stem} ({n}){extension}";
        return entry;
    }
}
