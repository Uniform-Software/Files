using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace UniFiles.App.Services;

/// <summary>
/// Plays a video with LibVLC (sound included) and draws its frames into a <see cref="WriteableBitmap"/>.
/// Rendering through callbacks rather than a native video window keeps the player a normal Avalonia
/// image: rounded corners, overlays and identical behavior on Windows, macOS and Linux.
/// LibVLC calls the callbacks on its own threads; nothing here may call back into the player from them.
/// </summary>
public sealed class VideoPlayer : IDisposable
{
    private const int MaxWidth = 1280;

    private static readonly Lazy<LibVLC> Engine = new(() =>
    {
        LibVLCSharp.Shared.Core.Initialize();
        return new LibVLC("--quiet", "--no-video-title-show", "--no-snapshot-preview");
    });

    private readonly object _frameLock = new();
    private readonly MediaPlayer _player;
    private readonly Media _media;

    // Kept in fields so the garbage collector never frees a delegate LibVLC still calls.
    private readonly MediaPlayer.LibVLCVideoFormatCb _formatCallback;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanupCallback;
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCallback;
    private readonly MediaPlayer.LibVLCVideoUnlockCb _unlockCallback;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCallback;

    private IntPtr _buffer;
    private int _width;
    private int _height;
    private int _copyPending;
    private readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _ended;
    private bool _disposed;

    private VideoPlayer(string path)
    {
        _formatCallback = OnFormat;
        _cleanupCallback = OnCleanup;
        _lockCallback = OnLock;
        _unlockCallback = OnUnlock;
        _displayCallback = OnDisplay;

        _player = new MediaPlayer(Engine.Value);
        _player.SetVideoFormatCallbacks(_formatCallback, _cleanupCallback);
        _player.SetVideoCallbacks(_lockCallback, _unlockCallback, _displayCallback);
        _player.EndReached += (_, _) => _ended = true;

        _media = new Media(Engine.Value, new Uri(path));
        _media.AddOption(":start-paused");
        _player.Media = _media;
    }

    /// <summary>The latest frame; replaced when the video size is known.</summary>
    public WriteableBitmap? Frame { get; private set; }

    /// <summary>Raised on the UI thread after a new frame was copied into <see cref="Frame"/>.</summary>
    public event EventHandler? FrameUpdated;

    public bool IsPlaying => _player.IsPlaying;

    /// <summary>Current position in seconds.</summary>
    public double Position => Math.Max(0, _player.Time / 1000.0);

    /// <summary>
    /// Loads the engine and the video, and shows its first frame, paused.
    /// Throws if LibVLC isn't available (e.g. Linux without the "vlc" package).
    /// </summary>
    public static async Task<VideoPlayer> OpenAsync(string path)
    {
        var player = await Task.Run(() => new VideoPlayer(path));
        await player.ShowFirstFrameAsync();
        return player;
    }

    private async Task ShowFirstFrameAsync()
    {
        // ":start-paused" opens the video silently, paused; then ask for one picture.
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPaused(object? sender, EventArgs e) => paused.TrySetResult();
        _player.Paused += OnPaused;
        await Task.Run(() => _player.Play());
        await Task.WhenAny(paused.Task, Task.Delay(3000));
        _player.Paused -= OnPaused;
        await Task.Run(() => _player.NextFrame());
        await Task.WhenAny(_firstFrame.Task, Task.Delay(3000));
    }

    public void Play()
    {
        if (_disposed)
            return;
        if (_ended)
        {
            // After the end LibVLC stops: restart from the beginning.
            _ended = false;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                _player.Stop();
                _player.Play();
            });
            return;
        }
        _player.SetPause(false);
        if (!_player.IsPlaying)
            _player.Play();
    }

    public void Pause()
    {
        if (!_disposed && _player.IsPlaying)
            _player.SetPause(true);
    }

    public void Seek(double seconds)
    {
        if (_disposed)
            return;
        var time = (long)(Math.Max(0, seconds) * 1000);
        if (_ended)
        {
            _ended = false;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                // Restarts paused (":start-paused"), then jumps to the requested time.
                _player.Stop();
                _player.Play();
                _player.Time = time;
            });
            return;
        }
        // While paused, LibVLC doesn't redraw after a seek: the editor shows a still from ffmpeg instead.
        _player.Time = time;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var player = _player;
        var media = _media;
        // Stop() waits for LibVLC's threads: never block the UI with it.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            player.Stop();
            player.Dispose();
            media.Dispose();
            lock (_frameLock)
            {
                if (_buffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
            }
        });
    }

    // ---- LibVLC callbacks (LibVLC threads) ---------------------------------------------

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        // Ask for 32-bit BGRA ("RV32"), scaled down to keep copies cheap.
        Marshal.Copy("RV32"u8.ToArray(), 0, chroma, 4);
        if (width > MaxWidth)
        {
            height = (uint)(height * MaxWidth / (double)width) & ~1u;
            width = MaxWidth;
        }
        pitches = width * 4;
        lines = height;

        lock (_frameLock)
        {
            if (_buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(_buffer);
            _width = (int)width;
            _height = (int)height;
            _buffer = Marshal.AllocHGlobal((int)(pitches * lines));
        }

        var (w, h) = (_width, _height);
        Dispatcher.UIThread.Post(() =>
            Frame = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque));
        return 1;
    }

    private void OnCleanup(ref IntPtr opaque) { }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        Monitor.Enter(_frameLock);
        Marshal.WriteIntPtr(planes, _buffer);
        return IntPtr.Zero;
    }

    private void OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes) => Monitor.Exit(_frameLock);

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        // At most one pending copy: if the UI is busy, frames are skipped rather than queued.
        if (Interlocked.Exchange(ref _copyPending, 1) == 0)
            Dispatcher.UIThread.Post(CopyFrame, DispatcherPriority.Render);
    }

    // ---- UI thread ----------------------------------------------------------------------

    private unsafe void CopyFrame()
    {
        Interlocked.Exchange(ref _copyPending, 0);
        if (Frame is not { } frame || _disposed)
            return;

        lock (_frameLock)
        {
            if (_buffer == IntPtr.Zero || frame.PixelSize.Width != _width || frame.PixelSize.Height != _height)
                return;
            using var target = frame.Lock();
            var rowBytes = _width * 4;
            for (var y = 0; y < _height; y++)
            {
                Buffer.MemoryCopy(
                    (byte*)_buffer + (long)y * rowBytes,
                    (byte*)target.Address + (long)y * target.RowBytes,
                    target.RowBytes, rowBytes);
            }
        }
        FrameUpdated?.Invoke(this, EventArgs.Empty);
        _firstFrame.TrySetResult();
    }
}
