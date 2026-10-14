using System.Text.Json;
using UniFiles.Core;
using UniFiles.Core.Jobs;
using UniFiles.Core.Resources;
using UniFiles.Core.Tools.Images;
using UniFiles.Core.Tools.Pdf;
using UniFiles.Core.Tools.Video;
using UniFiles.Core.Storage;
using UniFiles.Core.Tools;

namespace UniFiles.App.Services;

/// <summary>The app's long-lived objects, created once at startup.</summary>
public sealed class AppServices
{
    private static readonly Lazy<AppServices> Instance = new(() => new AppServices());

    private AppServices()
    {
        SettingsStore = new SettingsStore();
        Settings = SettingsStore.Load();
        VideoEncoder.HardwareEnabled = Settings.HardwareAcceleration;
        History = new HistoryStore();
        Queue.JobFinished += (_, job) =>
        {
            try
            {
                History.Add(job);
            }
            catch (Exception)
            {
                // History is a convenience: a locked database must never break a job.
            }
        };
    }

    public static AppServices Current => Instance.Value;

    public ToolCatalog Catalog { get; } = new();

    public JobQueue Queue { get; } = new(maxConcurrentJobs: 2);

    public SettingsStore SettingsStore { get; }

    public AppSettings Settings { get; }

    public HistoryStore History { get; }

    public Updater Updater { get; } = new();

    public OutputFolder OutputFolder => new(Settings.OutputDirectory);

    /// <summary>The settings last used with a tool, or its defaults.</summary>
    public ToolSettings LoadToolSettings(IFileTool tool) =>
        SettingsFrom(tool, Settings.ToolSettings.GetValueOrDefault(tool.Id));

    /// <summary>The tool's defaults, overridden by saved values of the right type (unknown keys are ignored).</summary>
    public static ToolSettings SettingsFrom(IFileTool tool, IReadOnlyDictionary<string, JsonElement>? saved)
    {
        var settings = ToolSettings.FromDefaults(tool);
        if (saved is null)
            return settings;

        foreach (var option in tool.Options)
        {
            if (!saved.TryGetValue(option.Key, out var value))
                continue;
            object? restored = (option, value.ValueKind) switch
            {
                (NumberOption, JsonValueKind.Number) when value.TryGetInt32(out var number) => number,
                (BoolOption, JsonValueKind.True or JsonValueKind.False) => value.GetBoolean(),
                (ChoiceOption choice, JsonValueKind.String) when choice.Choices.Any(c => c.Value == value.GetString()) => value.GetString(),
                (TextOption, JsonValueKind.String) => value.GetString(),
                _ => null,
            };
            if (restored is not null)
                settings[option.Key] = restored;
        }
        return settings;
    }

    public void SaveToolSettings(IFileTool tool, ToolSettings settings)
    {
        Settings.ToolSettings[tool.Id] = Serialize(settings);
        SaveSettings();
    }

    public static Dictionary<string, JsonElement> Serialize(ToolSettings settings) =>
        settings.Values.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));

    /// <summary>Raised after a preset is added, replaced or deleted (the right-click menu lists them).</summary>
    public event EventHandler? PresetsChanged;

    /// <summary>Saves a preset; one with the same name for the same tool is replaced.</summary>
    public ToolPreset SavePreset(IFileTool tool, string name, ToolSettings settings)
    {
        var preset = Settings.Presets.FirstOrDefault(p => p.ToolId == tool.Id && string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (preset is null)
        {
            preset = new ToolPreset { ToolId = tool.Id };
            Settings.Presets.Add(preset);
        }
        preset.Name = name;
        preset.Values = Serialize(settings);
        SaveSettings();
        PresetsChanged?.Invoke(this, EventArgs.Empty);
        return preset;
    }

    /// <summary>Renames a preset; false if another preset of the same tool already has that name.</summary>
    public bool RenamePreset(ToolPreset preset, string name)
    {
        if (Settings.Presets.Any(p => p != preset && p.ToolId == preset.ToolId && string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            return false;
        preset.Name = name;
        SaveSettings();
        PresetsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void DeletePreset(ToolPreset preset)
    {
        if (Settings.Presets.Remove(preset))
        {
            SaveSettings();
            PresetsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A preset and its tool, if the tool still exists.</summary>
    public (IFileTool Tool, ToolSettings Settings)? ResolvePreset(string presetId) =>
        Settings.Presets.FirstOrDefault(p => p.Id == presetId) is { } preset && Catalog.Find(preset.ToolId) is { } tool
            ? (tool, SettingsFrom(tool, preset.Values))
            : null;

    /// <summary>Plain-language warning when a tool needs a program that isn't installed, else null.</summary>
    public static string? MissingProgramWarning(IFileTool? tool) => tool switch
    {
        { Module: ToolModule.Video } or ImageFromHeicTool when ExternalTools.FindFfmpeg() is null =>
            string.Format(Strings.Warning_ProgramMissing, "ffmpeg"),
        PdfCompressTool when ExternalTools.FindGhostscript() is null =>
            string.Format(Strings.Warning_ProgramMissing, "Ghostscript"),
        _ => null,
    };

    public void SaveSettings()
    {
        try
        {
            SettingsStore.Save(Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preferences stay in memory for this session.
        }
    }
}
