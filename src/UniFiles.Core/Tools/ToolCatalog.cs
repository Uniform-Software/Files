using UniFiles.Core.Tools.Files;
using UniFiles.Core.Tools.Images;
using UniFiles.Core.Tools.Pdf;
using UniFiles.Core.Tools.Rename;
using UniFiles.Core.Tools.Video;

namespace UniFiles.Core.Tools;

/// <summary>Every available tool, in display order. Register new tools here.</summary>
public sealed class ToolCatalog
{
    public ToolCatalog()
    {
        Tools =
        [
            new ImageCompressTool(),
            new ImageResizeTool(),
            new ImageConvertTool(),
            new ImageFromHeicTool(),
            new ImageStripMetadataTool(),
            new BatchRenameTool(),
            new PdfMergeTool(),
            new PdfSplitTool(),
            new PdfOrganizeTool(),
            new PdfToImagesTool(),
            new PdfStripMetadataTool(),
            new PdfFormFillTool(),
            new PdfFormFlattenTool(),
            new PdfFormExportTool(),
            new ImagesToPdfTool(),
            new PdfCompressTool(),
            new VideoTrimTool(),
            new VideoCompressTool(),
            new VideoConvertTool(),
            new AudioExtractTool(),
            new AudioNormalizeTool(),
            new VideoMuteTool(),
            new ZipTool(),
        ];
    }

    public IReadOnlyList<IFileTool> Tools { get; }

    public IFileTool? Find(string id) => Tools.FirstOrDefault(t => t.Id == id);

    public IEnumerable<IFileTool> ForModule(ToolModule module) => Tools.Where(t => t.Module == module);

    /// <summary>Tools that accept every one of the given files.</summary>
    public IEnumerable<IFileTool> CompatibleWith(IReadOnlyCollection<string> files)
    {
        if (files.Count == 0)
            return [];
        return Tools.Where(t =>
            files.Count >= t.MinimumInputs
            && files.Count <= t.MaximumInputs
            && files.All(t.Accepts));
    }

    /// <summary>File extensions a module can open, for file pickers.</summary>
    public static IReadOnlyList<string> PatternsFor(ToolModule module) => module switch
    {
        ToolModule.Images => ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.avif", "*.heic", "*.gif", "*.tif", "*.tiff", "*.bmp"],
        ToolModule.Pdf => ["*.pdf", "*.jpg", "*.jpeg", "*.png", "*.webp"],
        ToolModule.Video => ["*.mp4", "*.mov", "*.mkv", "*.webm", "*.avi", "*.m4v", "*.mp3", "*.wav", "*.flac", "*.m4a", "*.ogg"],
        _ => ["*"],
    };
}
