using Avalonia;
using Avalonia.Controls;
using UniFiles.App.Platform;
using UniFiles.App.Services;
using UniFiles.Core.Storage;
using UniFiles.Core.Tools;

namespace UniFiles.App;

internal static class Program
{
    /// <summary>What this launch asked for; handled once the UI is ready.</summary>
    public static LaunchRequest InitialRequest { get; private set; } = new(LaunchMode.Open, []);

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called.
    [STAThread]
    public static int Main(string[] args)
    {
        var request = LaunchRequest.Parse(args);

        // The language must be set before any text is read from the resources, including the
        // tool settings built when the services are created: read it straight from the file.
        var settingsStore = new SettingsStore();
        Localization.Apply(settingsStore.Load().Language);

        if (request.Mode is LaunchMode.Register or LaunchMode.Unregister)
            return SetContextMenu(settingsStore, request.Mode == LaunchMode.Register);

        // Already running: hand the request over (right-click on 20 files = 20 launches, one job).
        if (!SingleInstance.TryBecomePrimary() && SingleInstance.Send(request))
            return 0;

        InitialRequest = request;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>Installer hooks: <c>--register</c> / <c>--unregister</c>.</summary>
    private static int SetContextMenu(SettingsStore store, bool enabled)
    {
        var installer = ExplorerMenu.Installer;
        if (enabled && Environment.ProcessPath is { } exe)
            installer.Install(exe, new ToolCatalog());
        else
            installer.Uninstall();

        var settings = store.Load();
        settings.ContextMenuEnabled = enabled && installer.IsSupported;
        store.Save(settings);
        return 0;
    }
}
