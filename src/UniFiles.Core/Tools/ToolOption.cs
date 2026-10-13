namespace UniFiles.Core.Tools;

/// <summary>
/// Describes one setting of a tool. The UI picks a control from the concrete type
/// (list, slider, switch, text box), so tools never reference the UI.
/// </summary>
public abstract record ToolOption(string Key, string Label)
{
    public abstract object DefaultValue { get; }

    /// <summary>Optional key of a boolean or choice option that must be enabled for this one to show.</summary>
    public string? VisibleWhenKey { get; init; }

    /// <summary>Value of <see cref="VisibleWhenKey"/> for which this option shows. Null means "true".</summary>
    public string? VisibleWhenValue { get; init; }

    public string? Hint { get; init; }
}

public sealed record Choice(string Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record ChoiceOption(string Key, string Label, IReadOnlyList<Choice> Choices, string Default)
    : ToolOption(Key, Label)
{
    public override object DefaultValue => Default;
}

public sealed record NumberOption(string Key, string Label, int Minimum, int Maximum, int Default, string? Unit = null)
    : ToolOption(Key, Label)
{
    public override object DefaultValue => Default;
}

public sealed record BoolOption(string Key, string Label, bool Default)
    : ToolOption(Key, Label)
{
    public override object DefaultValue => Default;
}

public sealed record TextOption(string Key, string Label, string Default)
    : ToolOption(Key, Label)
{
    public override object DefaultValue => Default;
}

/// <summary>Values chosen by the user, keyed by <see cref="ToolOption.Key"/>.</summary>
public sealed class ToolSettings
{
    private readonly Dictionary<string, object?> _values;

    public ToolSettings(IReadOnlyDictionary<string, object?>? values = null)
    {
        _values = values is null ? new() : new(values);
    }

    public static ToolSettings FromDefaults(IFileTool tool) =>
        new(tool.Options.ToDictionary(o => o.Key, o => (object?)o.DefaultValue));

    public IReadOnlyDictionary<string, object?> Values => _values;

    public object? this[string key]
    {
        get => _values.GetValueOrDefault(key);
        set => _values[key] = value;
    }

    public string GetString(string key, string fallback = "") =>
        _values.GetValueOrDefault(key)?.ToString() ?? fallback;

    public int GetInt(string key, int fallback = 0) => _values.GetValueOrDefault(key) switch
    {
        int i => i,
        double d => (int)Math.Round(d),
        decimal m => (int)Math.Round(m),
        string s when int.TryParse(s, out var parsed) => parsed,
        _ => fallback,
    };

    public bool GetBool(string key, bool fallback = false) => _values.GetValueOrDefault(key) switch
    {
        bool b => b,
        string s when bool.TryParse(s, out var parsed) => parsed,
        _ => fallback,
    };

    public ToolSettings Clone() => new(_values);
}
