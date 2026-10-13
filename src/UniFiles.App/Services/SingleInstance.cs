using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace UniFiles.App.Services;

/// <summary>
/// Keeps a single UniFiles running per user session. Later launches (right-click menu, desktop
/// shortcut, Dock) send their request to the running instance over a local named pipe and exit.
/// The pipe only accepts connections from the same user.
/// </summary>
public static class SingleInstance
{
    private const int ListenerCount = 4;

    private static readonly string Name = BuildName();
    private static readonly object Sync = new();
    private static readonly Queue<LaunchRequest> Pending = new();
    private static Mutex? _mutex;
    private static Action<LaunchRequest>? _handler;

    /// <summary>True if this process is the first one; it then starts listening right away.</summary>
    public static bool TryBecomePrimary()
    {
        _mutex = new Mutex(initiallyOwned: true, Name, out var createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        for (var i = 0; i < ListenerCount; i++)
            _ = Task.Run(ListenAsync);
        return true;
    }

    /// <summary>
    /// Delivers requests to <paramref name="handler"/>, including those received before the UI was ready.
    /// The handler is called on a background thread.
    /// </summary>
    public static void Attach(Action<LaunchRequest> handler)
    {
        lock (Sync)
        {
            _handler = handler;
            while (Pending.TryDequeue(out var request))
                handler(request);
        }
    }

    /// <summary>Sends a request to the primary instance. Waits a few seconds if it is still starting.</summary>
    public static bool Send(LaunchRequest request)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeout: 10_000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.WriteLine(request.Serialize());
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task ListenAsync()
    {
        while (true)
        {
            try
            {
                await using var server = new NamedPipeServerStream(Name, PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync();
                using var reader = new StreamReader(server, Encoding.UTF8);
                if (await reader.ReadLineAsync() is { } line && LaunchRequest.Deserialize(line) is { } request)
                {
                    lock (Sync)
                    {
                        if (_handler is { } handler)
                            handler(request);
                        else
                            Pending.Enqueue(request);
                    }
                }
            }
            catch (IOException)
            {
                // A client that hung up mid-message: wait for the next one.
            }
        }
    }

    private static string BuildName()
    {
        // Per user, per session and per data folder (a portable copy is a separate app);
        // hashed so the name is valid everywhere (Windows pipes, Unix sockets).
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var dataFolder = Path.GetFullPath(UniFiles.Core.Storage.SettingsStore.AppDataDirectory).ToLowerInvariant();
        var id = $"{Environment.UserName}|{process.SessionId}|{dataFolder}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..16];
        return $"UniFiles-{hash}";
    }
}
