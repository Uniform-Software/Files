using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniFiles.App.Controls;
using UniFiles.App.Services;
using UniFiles.Core.Resources;
using UniFiles.Core.Tools;
using UniFiles.Core.Tools.Video;

namespace UniFiles.App.ViewModels;

/// <summary>
/// Simplified video editor: play, cut at the playhead, remove or keep passages, export.
/// The preview skips removed passages, so what you watch is what you get.
/// </summary>
public partial class TrimEditorViewModel : PageViewModel, IDisposable
{
    private const int ThumbnailCount = 16;
    private const int ThumbnailHeight = 72;

    /// <summary>Cuts closer than this to an existing one are ignored.</summary>
    private const double MinimumPassage = 0.2;

    private readonly MainWindowViewModel _main;
    private readonly AppServices _services;
    private readonly VideoTrimTool _tool;
    private readonly PageViewModel _back;
    private readonly Stack<IReadOnlyList<TimelineSegment>> _undo = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly DispatcherTimer _clock;
    private VideoPlayer? _player;
    private bool _syncingFromPlayer;
    private CancellationTokenSource? _stillRequest;
    private bool _showingStill;

    public TrimEditorViewModel(MainWindowViewModel main, AppServices services, VideoTrimTool tool, string file, PageViewModel back)
    {
        _main = main;
        _services = services;
        _tool = tool;
        _back = back;
        FilePath = file;
        FileName = Path.GetFileName(file);
        _segments = [];
        _thumbnails = new Bitmap?[ThumbnailCount];

        // Follows the player ~30 times a second for a smooth playhead.
        _clock = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => Tick());
        _ = LoadAsync();
    }

    public string FilePath { get; }

    public string FileName { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText), nameof(PositionText))]
    private double _duration;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText), nameof(CurrentSegmentKept), nameof(ToggleLabel))]
    [NotifyCanExecuteChangedFor(nameof(SplitCommand), nameof(ToggleSegmentCommand))]
    private double _position;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(CanExportFast), nameof(CurrentSegmentKept), nameof(ToggleLabel))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand), nameof(ResetCommand), nameof(ToggleSegmentCommand))]
    private IReadOnlyList<TimelineSegment> _segments;

    [ObservableProperty]
    private IReadOnlyList<Bitmap?> _thumbnails;

    [ObservableProperty]
    private int _thumbnailVersion;

    [ObservableProperty]
    private Bitmap? _frame;

    [ObservableProperty]
    private int _frameVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseLabel))]
    private bool _isPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    private bool _isLoading = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlayerError))]
    private string? _playerError;

    [ObservableProperty]
    private bool _exportFast;

    public bool IsReady => !IsLoading && Duration > 0;

    public bool HasPlayerError => PlayerError is not null;

    public string PlayPauseLabel => IsPlaying ? Strings.Editor_Pause : Strings.Editor_Play;

    public string DurationText => Timeline.FormatTime(Duration);

    public string PositionText => $"{Timeline.FormatTime(Position)} / {Timeline.FormatTime(Duration)}";

    /// <summary>Whether the passage under the playhead is kept (drives the Remove/Keep button).</summary>
    public bool CurrentSegmentKept => SegmentAt(Position)?.Kept ?? true;

    public string ToggleLabel => CurrentSegmentKept ? Strings.Editor_Remove : Strings.Editor_Keep;

    /// <summary>Copying without re-encoding is only possible for a single kept passage.</summary>
    public bool CanExportFast => KeptRanges().Count == 1;

    public string Summary
    {
        get
        {
            var kept = KeptRanges();
            var removed = Segments.Count(s => !s.Kept);
            var text = string.Format(Strings.Editor_FinalDuration, Timeline.FormatTime(kept.Sum(r => r.Duration)));
            return removed == 0 ? text : $"{text} · {string.Format(Strings.Editor_Removed, removed)}";
        }
    }

    // Called by the generated setter only when the value changes.
    partial void OnPositionChanged(double value)
    {
        if (_syncingFromPlayer)
            return;
        _player?.Seek(value);
        if (_player is null || !_player.IsPlaying)
            _ = ShowStillAsync(value);
    }

    /// <summary>Paused preview at <paramref name="seconds"/>, from ffmpeg. Only the latest request wins.</summary>
    private async Task ShowStillAsync(double seconds)
    {
        _stillRequest?.Cancel();
        var request = _stillRequest = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
        try
        {
            // While dragging, wait for the pointer to settle a little.
            await Task.Delay(60, request.Token);
            var file = await VideoThumbnails.ExtractFrameAsync(FilePath, seconds, 720, request.Token);
            if (file is null || request.IsCancellationRequested || IsPlaying)
                return;
            _showingStill = true;
            Frame = new Bitmap(file);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // No still: the previous picture stays.
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            var info = await VideoThumbnails.ProbeAsync(FilePath, _cancellation.Token);
            Duration = info.Duration.TotalSeconds;
            Segments = [new TimelineSegment(0, Duration, true)];
            _ = LoadThumbnailsAsync(info.Duration);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PlayerError = ex is ToolException tool ? tool.Message : Strings.Error_MediaUnreadable;
            IsLoading = false;
            return;
        }

        try
        {
            _player = await VideoPlayer.OpenAsync(FilePath);
            _player.FrameUpdated += (_, _) =>
            {
                // Paused on an ffmpeg still: keep it until playback resumes.
                if (_showingStill && _player?.IsPlaying != true)
                    return;
                _showingStill = false;
                Frame = _player?.Frame;
                FrameVersion++;
            };
            Frame = _player.Frame;
            _clock.Start();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No playback (e.g. Linux without VLC): the filmstrip still allows cutting.
            PlayerError = string.Format(Strings.Editor_PlayerUnavailable, ex.Message);
        }
        IsLoading = false;
    }

    private async Task LoadThumbnailsAsync(TimeSpan duration)
    {
        try
        {
            await VideoThumbnails.ExtractAsync(FilePath, duration, ThumbnailCount, ThumbnailHeight, (index, file) =>
                Dispatcher.UIThread.Post(() =>
                {
                    var list = Thumbnails.ToArray();
                    list[index] = new Bitmap(file);
                    Thumbnails = list;
                    ThumbnailVersion++;
                }), _cancellation.Token);
        }
        catch (Exception)
        {
            // Thumbnails are a comfort: the timeline works without them.
        }
    }

    private void Tick()
    {
        if (_player is null)
            return;
        IsPlaying = _player.IsPlaying;
        if (!IsPlaying)
            return;

        var position = _player.Position;
        // Removed passages are skipped while previewing.
        if (SegmentAt(position) is { Kept: false } removed)
        {
            var next = Segments.FirstOrDefault(s => s.Kept && s.Start >= removed.End - 0.01);
            if (next is null)
            {
                _player.Pause();
                SetPositionFromPlayer(removed.Start);
                return;
            }
            _player.Seek(next.Start);
            position = next.Start;
        }
        SetPositionFromPlayer(position);
    }

    private void SetPositionFromPlayer(double seconds)
    {
        _syncingFromPlayer = true;
        Position = Math.Min(seconds, Duration);
        _syncingFromPlayer = false;
    }

    private TimelineSegment? SegmentAt(double seconds) =>
        Segments.FirstOrDefault(s => seconds >= s.Start && seconds < s.End) ?? Segments.LastOrDefault();

    private List<TimeRange> KeptRanges()
    {
        // Adjacent kept passages form a single range.
        var ranges = new List<TimeRange>();
        foreach (var segment in Segments.Where(s => s.Kept))
        {
            if (ranges.Count > 0 && Math.Abs(ranges[^1].End - segment.Start) < 0.001)
                ranges[^1] = ranges[^1] with { End = segment.End };
            else
                ranges.Add(new TimeRange(segment.Start, segment.End));
        }
        return ranges;
    }

    private void Edit(IReadOnlyList<TimelineSegment> segments)
    {
        _undo.Push(Segments);
        Segments = segments;
        UndoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (_player is null)
            return;
        if (_player.IsPlaying)
        {
            _player.Pause();
            IsPlaying = false;
            return;
        }
        // Starting inside a removed passage, or at the very end: begin at the next kept one.
        if (SegmentAt(Position) is { Kept: false } || Position >= Duration - 0.05)
        {
            var next = Segments.FirstOrDefault(s => s.Kept && s.Start >= Position) ?? Segments.FirstOrDefault(s => s.Kept);
            if (next is null)
                return;
            Position = next.Start;
        }
        _stillRequest?.Cancel();
        _player.Play();
        IsPlaying = true;
    }

    [RelayCommand]
    private void Step(string direction)
    {
        var seconds = direction == "back" ? -1 : 1;
        Position = Math.Clamp(Position + seconds, 0, Duration);
    }

    private bool CanSplit() =>
        Segments.Any(s => Position - s.Start >= MinimumPassage && s.End - Position >= MinimumPassage);

    /// <summary>Cuts the passage under the playhead in two.</summary>
    [RelayCommand(CanExecute = nameof(CanSplit))]
    private void Split()
    {
        var at = Position;
        var list = new List<TimelineSegment>();
        foreach (var s in Segments)
        {
            if (at - s.Start >= MinimumPassage && s.End - at >= MinimumPassage)
            {
                list.Add(s with { End = at });
                list.Add(s with { Start = at });
            }
            else
            {
                list.Add(s);
            }
        }
        Edit(list);
    }

    private bool CanToggleSegment() => Segments.Count > 1 || !CurrentSegmentKept;

    /// <summary>Removes the passage under the playhead, or brings it back.</summary>
    [RelayCommand(CanExecute = nameof(CanToggleSegment))]
    private void ToggleSegment()
    {
        if (SegmentAt(Position) is not { } current)
            return;
        Edit(Segments.Select(s => s == current ? s with { Kept = !s.Kept } : s).ToList());
    }

    private bool CanUndo() => _undo.Count > 0;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_undo.TryPop(out var previous))
            Segments = previous;
        UndoCommand.NotifyCanExecuteChanged();
    }

    private bool CanReset() => Segments.Count > 1;

    [RelayCommand(CanExecute = nameof(CanReset))]
    private void Reset() => Edit([new TimelineSegment(0, Duration, true)]);

    private bool CanExport() => IsReady && KeptRanges().Sum(r => r.Duration) >= MinimumPassage;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void Export()
    {
        var settings = _services.LoadToolSettings(_tool);
        settings[VideoTrimTool.SegmentsKey] = VideoTrimTool.FormatSegments(KeptRanges());
        settings["precise"] = !(ExportFast && CanExportFast);
        _services.Queue.Enqueue(_tool, [FilePath], settings, _services.OutputFolder);
        Dispose();
        _main.ShowQueue();
    }

    [RelayCommand]
    private void Back()
    {
        Dispose();
        _main.ShowPage(_back);
    }

    partial void OnIsLoadingChanged(bool value) => ExportCommand.NotifyCanExecuteChanged();

    public void Dispose()
    {
        _clock.Stop();
        _cancellation.Cancel();
        _player?.Dispose();
        _player = null;
        IsPlaying = false;
    }
}
