using UniFiles.Core.Tools;

namespace UniFiles.Core.Automation;

/// <summary>What happens to an original once it has been processed successfully.</summary>
public enum AfterProcessing
{
    Keep,
    MoveToProcessed,
    Trash,
}

/// <summary>
/// An automation: every new file in <see cref="Folder"/> is processed with a preset. Saved in the
/// preferences (<c>AppSettings.WatchRules</c>).
/// </summary>
public sealed class WatchRule
{
    /// <summary>At most this many watched folders (enough for everyday use, keeps the app light).</summary>
    public const int MaximumCount = 10;

    /// <summary>Sub-folder receiving the originals with <see cref="AfterProcessing.MoveToProcessed"/>.</summary>
    public const string ProcessedFolderName = "Traités";

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public string Folder { get; set; } = "";

    public string PresetId { get; set; } = "";

    /// <summary>Where the results go (the preset's own destination is not used).</summary>
    public OutputMode OutputMode { get; set; } = OutputMode.Default;

    public string? CustomOutput { get; set; }

    public AfterProcessing After { get; set; } = AfterProcessing.Keep;

    public bool Enabled { get; set; } = true;

    public bool IncludeSubfolders { get; set; }

    public string ProcessedFolder => Path.Combine(Folder, ProcessedFolderName);
}
