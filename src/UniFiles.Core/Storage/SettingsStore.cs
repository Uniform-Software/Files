using System.Text.Json;
using System.Text.Json.Serialization;

namespace UniFiles.Core.Storage;

public enum AppTheme
{
    Dark,
    Light,
}

public sealed class AppSettings
{
    public static string DefaultOutputDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "UniFiles");

    public string OutputDirectory { get; set; } = DefaultOutputDirectory;

    public AppTheme Theme { get; set; } = AppTheme.Dark;

    /// <summary>"fr", "en" or "" to follow the system.</summary>
    public string Language { get; set; } = "";

    /// <summary>Turns animations off, in addition to the system setting.</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>Closing the window hides it in the notification area instead of quitting.</summary>
    public bool KeepInTray { get; set; } = true;

    /// <summary>The "UniFiles" submenu in the file explorer's right-click menu.</summary>
    public bool ContextMenuEnabled { get; set; }

    /// <summary>Last values chosen for each tool, keyed by tool id then option key.</summary>
    public Dictionary<string, Dictionary<string, JsonElement>> ToolSettings { get; set; } = [];
}

/// <summary>Reads and writes the preferences as JSON in the user's application data folder.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public SettingsStore(string? path = null)
    {
        FilePath = path ?? Path.Combine(AppDataDirectory, "settings.json");
    }

    /// <summary>
    /// Where preferences and history live. UNIFILES_DATA_DIR overrides it (portable use, development).
    /// </summary>
    public static string AppDataDirectory =>
        Environment.GetEnvironmentVariable("UNIFILES_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UniFiles");

    public string FilePath { get; }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged file must not prevent the app from starting: fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        // Write then swap, so a crash mid-write never leaves a truncated file.
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }
}
