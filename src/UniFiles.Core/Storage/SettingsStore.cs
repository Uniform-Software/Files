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

    /// <summary>Encodes videos with the computer's video chip when one is available.</summary>
    public bool HardwareAcceleration { get; set; } = true;

    /// <summary>Looks for a new version on GitHub at startup and installs it when the app quits.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>The "UniFiles" submenu in the file explorer's right-click menu.</summary>
    public bool ContextMenuEnabled { get; set; }

    /// <summary>Last values chosen for each tool, keyed by tool id then option key.</summary>
    public Dictionary<string, Dictionary<string, JsonElement>> ToolSettings { get; set; } = [];

    /// <summary>Named settings saved by the user ("Web 1600 px WebP"), in the order they were created.</summary>
    public List<ToolPreset> Presets { get; set; } = [];

    /// <summary>Watched folders (automations).</summary>
    public List<Automation.WatchRule> WatchRules { get; set; } = [];

    /// <summary>Start UniFiles when the session opens (needed for watched folders to keep working).</summary>
    public bool LaunchAtLogin { get; set; }

    /// <summary>Signing: last certificate, what a visible signature shows, time-stamping.</summary>
    public SigningSettings Signing { get; set; } = new();

    /// <summary>Version that ran last, to show its release notes once after an update.</summary>
    public string? LastVersion { get; set; }
}

/// <summary>Signing choices remembered from one signature to the next. Never a key or a password.</summary>
public sealed class SigningSettings
{
    /// <summary>Id (SHA-256) of the certificate used last.</summary>
    public string? LastIdentity { get; set; }

    public bool ShowName { get; set; } = true;

    public bool ShowDate { get; set; } = true;

    public bool ShowReason { get; set; } = true;

    /// <summary>"none", "drawn" or "image".</summary>
    public string Handwriting { get; set; } = "none";

    public string Reason { get; set; } = "";

    public string Location { get; set; } = "";

    /// <summary>Ask a time-stamping service to prove the date (sends a 32-byte digest, never the document).</summary>
    public bool Timestamp { get; set; }

    public string TimestampUrl { get; set; } = "";
}

/// <summary>A tool with saved settings, launched in one click (action screen, right-click menu).</summary>
public sealed class ToolPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public string Name { get; set; } = "";

    public string ToolId { get; set; } = "";

    /// <summary>Option values, keyed by option key. For a chain: its destination only.</summary>
    public Dictionary<string, JsonElement> Values { get; set; } = [];

    /// <summary>The actions of a chain, in order (empty for a single action). <see cref="ToolId"/> is then "chain".</summary>
    public List<PresetStep> Steps { get; set; } = [];

    [JsonIgnore]
    public bool IsChain => Steps.Count > 1;
}

/// <summary>One action of a chain preset and its settings.</summary>
public sealed class PresetStep
{
    public string ToolId { get; set; } = "";

    public Dictionary<string, JsonElement> Values { get; set; } = [];
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
    /// On macOS, LocalApplicationData is ~/Library/Application Support (ApplicationData would be ~/.config).
    /// </summary>
    public static string AppDataDirectory =>
        Environment.GetEnvironmentVariable("UNIFILES_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(OperatingSystem.IsMacOS()
                ? Environment.SpecialFolder.LocalApplicationData
                : Environment.SpecialFolder.ApplicationData), "UniFiles");

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
