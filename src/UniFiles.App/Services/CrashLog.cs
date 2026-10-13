using System.Text;
using Avalonia.Threading;
using UniFiles.Core.Storage;

namespace UniFiles.App.Services;

/// <summary>
/// Writes unexpected errors to a local file (logs\errors-yyyy-MM-dd.log in the data folder) so a bug
/// report can include them. Nothing is ever sent anywhere.
/// </summary>
public static class CrashLog
{
    private static readonly object Sync = new();

    public static string Folder => Path.Combine(SettingsStore.AppDataDirectory, "logs");

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("Unhandled", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("Unobserved task", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Called once Avalonia runs: UI-thread errors are logged, then the app carries on.</summary>
    public static void InstallForDispatcher() =>
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Write("UI thread", e.Exception);
            e.Handled = true;
        };

    public static void Write(string context, Exception? exception)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Folder);
                var file = Path.Combine(Folder, $"errors-{DateTime.Now:yyyy-MM-dd}.log");
                var text = new StringBuilder()
                    .AppendLine($"[{DateTime.Now:O}] {context} — UniFiles {typeof(CrashLog).Assembly.GetName().Version?.ToString(3)}, {Environment.OSVersion}")
                    .AppendLine(exception?.ToString() ?? "(no details)")
                    .AppendLine();
                File.AppendAllText(file, text.ToString());
            }
        }
        catch (Exception)
        {
            // Logging must never crash the app itself.
        }
    }
}
