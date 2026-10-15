using System.Reflection;
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
        var existed = File.Exists(SettingsStore.FilePath);
        Settings = SettingsStore.Load();
        if (CurrentVersion.ToString(3) != Settings.LastVersion)
        {
            // Settings from an earlier version (0.2 had no LastVersion): this launch follows an update.
            JustUpdated = existed;
            Settings.LastVersion = CurrentVersion.ToString(3);
            SaveSettings();
        }
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

    public static Version CurrentVersion { get; } = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>First launch of this version after an update: its release notes are shown once.</summary>
    public bool JustUpdated { get; }

    public AppSettings Settings { get; }

    public HistoryStore History { get; }

    public Updater Updater { get; } = new();

    /// <summary>Watched folders; started by the app once the window is ready.</summary>
    public AutomationService Automations => _automations ??= new AutomationService(this);

    private AutomationService? _automations;

    public OutputFolder OutputFolder => new(Settings.OutputDirectory);

    /// <summary>The settings last used with a tool, or its defaults.</summary>
    public ToolSettings LoadToolSettings(IFileTool tool) =>
        SettingsFrom(tool, Settings.ToolSettings.GetValueOrDefault(tool.Id));

    /// <summary>The tool's defaults, overridden by saved values of the right type (unknown keys are ignored).</summary>
    public static ToolSettings SettingsFrom(IFileTool tool, IReadOnlyDictionary<string, JsonElement>? saved) =>
        SavedSettings.FromSaved(tool, saved);

    /// <summary>Where the results of an action with these settings go.</summary>
    public OutputTarget TargetFor(ToolSettings settings) => OutputSettings.Read(settings, Settings.OutputDirectory);

    public void SaveToolSettings(IFileTool tool, ToolSettings settings)
    {
        Settings.ToolSettings[tool.Id] = Serialize(settings);
        SaveSettings();
    }

    public static Dictionary<string, JsonElement> Serialize(ToolSettings settings) => SavedSettings.ToSaved(settings);

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

    /// <summary>Saves a chain preset (its steps and destination); one with the same name is replaced.</summary>
    public ToolPreset SaveChainPreset(string name, IReadOnlyList<ChainStep> steps, ToolSettings destination)
    {
        var preset = Settings.Presets.FirstOrDefault(p => p.IsChain && string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (preset is null)
        {
            preset = new ToolPreset { ToolId = ChainTool.ChainId };
            Settings.Presets.Add(preset);
        }
        preset.Name = name;
        preset.Values = Serialize(destination);
        preset.Steps = steps.Select(s => new PresetStep
        {
            ToolId = s.Tool.Id,
            Values = Serialize(new ToolSettings(s.Settings.Values.Where(p => !OutputSettings.IsReserved(p.Key)).ToDictionary(p => p.Key, p => p.Value))),
        }).ToList();
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

    /// <summary>A preset's tool (or chain) and settings, if its tools still exist.</summary>
    public (IFileTool Tool, ToolSettings Settings)? ResolvePreset(string presetId) =>
        Settings.Presets.FirstOrDefault(p => p.Id == presetId) is { } preset ? PresetResolver.Resolve(Catalog, preset) : null;

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
