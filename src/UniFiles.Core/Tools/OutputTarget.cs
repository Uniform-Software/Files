namespace UniFiles.Core.Tools;

/// <summary>
/// A file to process and where it came from: <see cref="RootName"/> is the name of the folder that
/// was dropped (null for a file dropped on its own), <see cref="RelativeDirectory"/> its sub-folder
/// inside that folder ("Plage/2024", or "").
/// </summary>
public sealed record InputFile(string Path, string RelativeDirectory = "", string? RootName = null)
{
    public static InputFile Of(string path) => new(path);
}

public enum OutputMode
{
    /// <summary>The output folder of the preferences (Documents/UniFiles by default).</summary>
    Default,

    /// <summary>Each result next to its original.</summary>
    NextToOriginals,

    /// <summary>A sub-folder of the output folder named after the day ("2026-10-15").</summary>
    DatedSubfolder,

    /// <summary>A folder chosen for this action.</summary>
    Custom,
}

/// <summary>
/// Where the results of a job go. <see cref="BaseDirectory"/> is the preferences' output folder, or
/// the chosen folder for <see cref="OutputMode.Custom"/>. With <see cref="KeepTree"/>, files from a
/// dropped folder keep their sub-folders under the destination.
/// </summary>
public sealed record OutputTarget(OutputMode Mode, string BaseDirectory, bool KeepTree = false)
{
    /// <summary>The day used by <see cref="OutputMode.DatedSubfolder"/>, fixed when the job is created.</summary>
    public DateTime Date { get; init; } = DateTime.Now;

    /// <summary>A plain folder, as before 0.3 (one folder for every result).</summary>
    public static OutputTarget Fixed(string directory) => new(OutputMode.Custom, directory);

    /// <summary>Folder for the results of <paramref name="input"/>.</summary>
    public OutputFolder For(InputFile input)
    {
        if (Mode == OutputMode.NextToOriginals)
            return new OutputFolder(Path.GetDirectoryName(Path.GetFullPath(input.Path))!);

        var root = Root;
        if (!KeepTree || input.RootName is null)
            return new OutputFolder(root);

        var folder = Path.GetFullPath(Path.Combine(root, OutputFolder.Sanitize(input.RootName), input.RelativeDirectory));
        // A relative directory can't lead outside the destination ("..").
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return new OutputFolder(folder.StartsWith(rootWithSeparator, StringComparison.Ordinal) ? folder : root);
    }

    /// <summary>The destination as one folder (for the history and "Open folder"): the first input's for "next to originals".</summary>
    public OutputFolder Display(IReadOnlyList<InputFile> inputs) =>
        Mode == OutputMode.NextToOriginals && inputs.Count > 0 ? For(inputs[0]) : new OutputFolder(Root);

    private string Root => Mode == OutputMode.DatedSubfolder
        ? Path.Combine(BaseDirectory, Date.ToString("yyyy-MM-dd"))
        : BaseDirectory;
}

/// <summary>
/// The destination chosen for an action, kept with its settings (so presets remember it) under
/// reserved keys starting with "_", which no tool option uses.
/// </summary>
public static class OutputSettings
{
    public const string ModeKey = "_outputMode";
    public const string DirectoryKey = "_outputDirectory";
    public const string KeepTreeKey = "_keepTree";

    public static bool IsReserved(string key) => key.StartsWith('_');

    /// <summary>The destination for these settings; <paramref name="defaultDirectory"/> is the preferences' output folder.</summary>
    public static OutputTarget Read(ToolSettings settings, string defaultDirectory)
    {
        var mode = Enum.TryParse<OutputMode>(settings.GetString(ModeKey), out var parsed) ? parsed : OutputMode.Default;
        var custom = settings.GetString(DirectoryKey);
        if (mode == OutputMode.Custom && string.IsNullOrWhiteSpace(custom))
            mode = OutputMode.Default;
        return new OutputTarget(mode, mode == OutputMode.Custom ? custom : defaultDirectory, settings.GetBool(KeepTreeKey));
    }

    public static void Write(ToolSettings settings, OutputMode mode, string? customDirectory, bool keepTree)
    {
        settings[ModeKey] = mode.ToString();
        settings[DirectoryKey] = customDirectory ?? "";
        settings[KeepTreeKey] = keepTree;
    }
}
