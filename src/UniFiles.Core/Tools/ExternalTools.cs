using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using UniFiles.Core.Resources;

namespace UniFiles.Core.Tools;

/// <summary>
/// Locates the command-line programs UniFiles drives (ffmpeg, ffprobe, Ghostscript).
/// Looks first in the "tools" folder shipped next to the app, then in the usual install places.
/// </summary>
public static class ExternalTools
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static string? FindFfmpeg() => Find(["ffmpeg"]);

    public static string? FindFfprobe() => Find(["ffprobe"]);

    public static string? FindGhostscript() => Find(IsWindows ? ["gswin64c", "gswin32c", "gs"] : ["gs"], GhostscriptInstallDirs());

    public static string RequireFfmpeg() => FindFfmpeg() ?? throw Missing("ffmpeg");

    public static string RequireGhostscript() => FindGhostscript() ?? throw Missing("Ghostscript");

    private static ToolException Missing(string program) =>
        new(string.Format(Strings.Error_ProgramMissing, program));

    private static string? Find(IEnumerable<string> names, IEnumerable<string>? extraDirs = null)
    {
        var dirs = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "tools"),
            AppContext.BaseDirectory,
        };
        dirs.AddRange(extraDirs ?? []);
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        if (!IsWindows)
            dirs.AddRange(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"]);

        foreach (var name in names)
        {
            var fileName = IsWindows ? name + ".exe" : name;
            foreach (var dir in dirs)
            {
                var candidate = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    private static IEnumerable<string> GhostscriptInstallDirs()
    {
        if (!IsWindows)
            return [];
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "gs");
        if (!Directory.Exists(root))
            return [];
        // Newest version first, e.g. C:\Program Files\gs\gs10.05.1\bin
        return Directory.GetDirectories(root).OrderDescending().Select(d => Path.Combine(d, "bin"));
    }

    /// <summary>Runs a program to completion, killing it if the job is canceled.</summary>
    public static async Task<(int ExitCode, string Errors)> RunAsync(string program, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(program)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        var errors = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (errors) errors.AppendLine(e.Data); };
        process.OutputDataReceived += (_, _) => { };

        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        lock (errors)
            return (process.ExitCode, errors.ToString());
    }
}
