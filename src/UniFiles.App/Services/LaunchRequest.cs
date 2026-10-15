using System.Text.Json;

namespace UniFiles.App.Services;

public enum LaunchMode
{
    /// <summary>Show the window, with the files on the action screen if any.</summary>
    Open,

    /// <summary>Run a tool in the background with the last used settings, notify at the end.</summary>
    QuickAction,

    /// <summary>Add the right-click menu, then exit (installer hook).</summary>
    Register,

    /// <summary>Remove the right-click menu, then exit (uninstaller hook).</summary>
    Unregister,

    /// <summary>Start without a window, in the menu bar / notification area (opening of the session).</summary>
    Background,
}

/// <summary>
/// What a launch asks for, parsed from the command line:
/// <c>UniFiles [files]</c>, <c>UniFiles --action &lt;tool id&gt; files</c>, <c>UniFiles --preset &lt;preset id&gt; files</c>,
/// <c>UniFiles --register</c>, <c>UniFiles --unregister</c>.
/// </summary>
/// A preset quick action carries <see cref="PresetPrefix"/> + the preset id as <see cref="ToolId"/>.
public sealed record LaunchRequest(LaunchMode Mode, IReadOnlyList<string> Files, string? ToolId = null)
{
    public const string PresetPrefix = "preset:";

    public static LaunchRequest Parse(IReadOnlyList<string> args)
    {
        if (args.Count > 0)
        {
            switch (args[0])
            {
                case "--register":
                    return new(LaunchMode.Register, []);
                case "--unregister":
                    return new(LaunchMode.Unregister, []);
                case "--action" when args.Count >= 2:
                    return new(LaunchMode.QuickAction, Absolute(args.Skip(2)), args[1]);
                case "--preset" when args.Count >= 2:
                    return new(LaunchMode.QuickAction, Absolute(args.Skip(2)), PresetPrefix + args[1]);
                case "--open":
                    return new(LaunchMode.Open, Absolute(args.Skip(1)));
                case "--background":
                    return new(LaunchMode.Background, []);
            }
        }
        return new(LaunchMode.Open, Absolute(args));
    }

    // The secondary process may run in another working directory than the main one.
    private static List<string> Absolute(IEnumerable<string> paths) => paths.Select(Path.GetFullPath).ToList();

    public string Serialize() => JsonSerializer.Serialize(this);

    public static LaunchRequest? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<LaunchRequest>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
