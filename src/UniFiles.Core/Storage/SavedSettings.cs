using System.Text.Json;
using UniFiles.Core.Tools;

namespace UniFiles.Core.Storage;

/// <summary>Tool settings as stored in the preferences (JSON values), and back.</summary>
public static class SavedSettings
{
    /// <summary>The tool's defaults, overridden by saved values of the right type (unknown keys are ignored).</summary>
    public static ToolSettings FromSaved(IFileTool tool, IReadOnlyDictionary<string, JsonElement>? saved)
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
        // Reserved keys (destination…) aren't tool options: keep them as they were saved.
        foreach (var (key, value) in saved)
        {
            if (!OutputSettings.IsReserved(key))
                continue;
            object? restored = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.True or JsonValueKind.False => value.GetBoolean(),
                _ => null,
            };
            if (restored is not null)
                settings[key] = restored;
        }
        return settings;
    }

    public static Dictionary<string, JsonElement> ToSaved(ToolSettings settings) =>
        settings.Values.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));
}

/// <summary>Turns a preset back into what runs it: a tool (or a chain of tools) and its settings.</summary>
public static class PresetResolver
{
    /// <summary>Null when a tool of the preset no longer exists.</summary>
    public static (IFileTool Tool, ToolSettings Settings)? Resolve(ToolCatalog catalog, ToolPreset preset)
    {
        if (preset.Steps.Count > 1)
        {
            var steps = new List<ChainStep>();
            foreach (var step in preset.Steps)
            {
                if (catalog.Find(step.ToolId) is not { } stepTool)
                    return null;
                steps.Add(new ChainStep(stepTool, SavedSettings.FromSaved(stepTool, step.Values)));
            }
            if (ChainTool.Validate(steps.Select(s => s.Tool).ToList()) is not null)
                return null;
            var chain = new ChainTool(steps);
            // The chain's own settings hold its destination.
            return (chain, SavedSettings.FromSaved(chain, preset.Values));
        }
        return catalog.Find(preset.ToolId) is { } tool ? (tool, SavedSettings.FromSaved(tool, preset.Values)) : null;
    }
}
