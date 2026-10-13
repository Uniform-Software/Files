using System.Text.Json;
using UniFiles.Core;
using UniFiles.Core.Jobs;
using UniFiles.Core.Resources;
using UniFiles.Core.Tools.Pdf;
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

    public OutputFolder OutputFolder => new(Settings.OutputDirectory);

    /// <summary>The settings last used with a tool, or its defaults.</summary>
    public ToolSettings LoadToolSettings(IFileTool tool)
    {
        var settings = ToolSettings.FromDefaults(tool);
        if (!Settings.ToolSettings.TryGetValue(tool.Id, out var saved))
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
        Settings.ToolSettings[tool.Id] = settings.Values.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));
        SaveSettings();
    }

    /// <summary>Plain-language warning when a tool needs a program that isn't installed, else null.</summary>
    public static string? MissingProgramWarning(IFileTool? tool) => tool switch
    {
        { Module: ToolModule.Video } when ExternalTools.FindFfmpeg() is null =>
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
