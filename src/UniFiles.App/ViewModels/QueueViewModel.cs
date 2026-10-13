using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniFiles.App.Services;
using UniFiles.Core.Jobs;
using UniFiles.Core.Resources;

namespace UniFiles.App.ViewModels;

public partial class QueueViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly Dictionary<Guid, JobViewModel> _byId = [];

    public QueueViewModel(AppServices services)
    {
        _services = services;
        services.Queue.JobsChanged += (_, _) => Dispatcher.UIThread.Post(Sync);
    }

    public ObservableCollection<JobViewModel> Jobs { get; } = [];

    public bool IsEmpty => Jobs.Count == 0;

    public bool HasFinished => Jobs.Any(j => j.IsFinished);

    /// <summary>Mirrors the queue order into <see cref="Jobs"/>, reusing existing view models.</summary>
    private void Sync()
    {
        var jobs = _services.Queue.Jobs;
        var ids = jobs.Select(j => j.Id).ToHashSet();

        foreach (var stale in Jobs.Where(vm => !ids.Contains(vm.Job.Id)).ToList())
        {
            Jobs.Remove(stale);
            _byId.Remove(stale.Job.Id);
        }

        for (var i = 0; i < jobs.Count; i++)
        {
            if (!_byId.TryGetValue(jobs[i].Id, out var vm))
            {
                vm = new JobViewModel(jobs[i], _services.Queue);
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(JobViewModel.IsFinished))
                        OnPropertyChanged(nameof(HasFinished));
                };
                _byId[jobs[i].Id] = vm;
                Jobs.Insert(i, vm);
            }
            else
            {
                var current = Jobs.IndexOf(vm);
                if (current != i)
                    Jobs.Move(current, i);
            }
        }

        foreach (var vm in Jobs)
            vm.RefreshPosition();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasFinished));
    }

    [RelayCommand]
    private void ClearFinished() => _services.Queue.ClearFinished();
}

public sealed class FailedItemViewModel(string name, string error)
{
    public string Name { get; } = name;

    public string Error { get; } = error;
}

/// <summary>A job card in the queue. Copies the job's state on the UI thread, at most once per frame.</summary>
public partial class JobViewModel : ObservableObject
{
    private readonly JobQueue _queue;
    private int _refreshPending;

    public JobViewModel(Job job, JobQueue queue)
    {
        Job = job;
        _queue = queue;
        Module = ModuleInfo.For(job.Tool.Module);
        job.Changed += (_, _) =>
        {
            if (Interlocked.Exchange(ref _refreshPending, 1) == 0)
                Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Background);
        };
        Refresh();
    }

    public Job Job { get; }

    public ModuleInfo Module { get; }

    public string ToolName => Job.Tool.Name;

    public string FilesText => Format.Files(Job.Inputs.Count);

    public ObservableCollection<FailedItemViewModel> Failures { get; } = [];

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string? _detail;

    [ObservableProperty]
    private string? _summary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPause))]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPause))]
    private bool _isPending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    private bool _isFinished;

    [ObservableProperty]
    private bool _hasErrors;

    [ObservableProperty]
    private bool _hasOutputs;

    [ObservableProperty]
    private bool _canMoveUp;

    [ObservableProperty]
    private bool _canMoveDown;

    public bool IsActive => !IsFinished;

    public bool CanPause => IsRunning || IsPending;

    public string RetryText => string.Format(Strings.Queue_Retry, Failures.Count);

    private void Refresh()
    {
        Interlocked.Exchange(ref _refreshPending, 0);

        var status = Job.Status;
        IsRunning = status == JobStatus.Running;
        IsPaused = status == JobStatus.Paused;
        IsPending = status == JobStatus.Pending;
        IsFinished = Job.IsFinished;
        Progress = Job.Progress * 100;
        HasOutputs = Job.DoneCount > 0;

        StatusText = status switch
        {
            JobStatus.Pending => Strings.Status_Pending,
            JobStatus.Running => Strings.Status_Running,
            JobStatus.Paused => Strings.Status_Paused,
            JobStatus.Completed => Strings.Status_Completed,
            JobStatus.CompletedWithErrors => Strings.Status_CompletedWithErrors,
            JobStatus.Failed => Strings.Status_Failed,
            _ => Strings.Status_Canceled,
        };

        var processed = Job.Items.Count(i => i.Status is not (WorkStatus.Pending or WorkStatus.Running));
        Detail = IsFinished
            ? null
            : Job.CurrentItem is { } current
                ? $"{processed} / {Job.Items.Count} · {current}"
                : $"{processed} / {Job.Items.Count}";

        if (IsFinished)
        {
            var parts = new List<string> { string.Format(Strings.Queue_Processed, Format.Files(Job.DoneCount)) };
            if (Job.Tool.ShowsSizeChange && Job.InputBytes != Job.OutputBytes && Format.Gain(Job.InputBytes, Job.OutputBytes) is { } gain)
                parts.Add(gain);
            if (Job.FinishedAt - Job.StartedAt is { } elapsed)
                parts.Add(string.Format(Strings.Queue_Elapsed, elapsed.TotalSeconds < 60 ? $"{elapsed.TotalSeconds:0.#} s" : $"{elapsed:m\\:ss}"));
            Summary = string.Join(" · ", parts);

            if (Failures.Count == 0)
            {
                foreach (var item in Job.Items.Where(i => i.Status == WorkStatus.Failed))
                    Failures.Add(new FailedItemViewModel(item.DisplayName, item.Error ?? Strings.Error_Unknown));
                OnPropertyChanged(nameof(RetryText));
            }
            HasErrors = Failures.Count > 0;
        }

        RefreshPosition();
    }

    public void RefreshPosition()
    {
        var jobs = _queue.Jobs;
        var index = jobs.ToList().IndexOf(Job);
        var waiting = Job.StartedAt is null && !Job.IsFinished;
        CanMoveUp = waiting && index > 0 && jobs[index - 1].StartedAt is null;
        CanMoveDown = waiting && index >= 0 && index < jobs.Count - 1 && jobs[index + 1].StartedAt is null;
    }

    [RelayCommand]
    private void Pause() => Job.Pause();

    [RelayCommand]
    private void Resume() => Job.Resume();

    [RelayCommand]
    private void Cancel() => _queue.Cancel(Job);

    [RelayCommand]
    private void MoveUp() => _queue.Move(Job, -1);

    [RelayCommand]
    private void MoveDown() => _queue.Move(Job, +1);

    [RelayCommand]
    private void Retry() => _queue.RetryFailed(Job);

    [RelayCommand]
    private Task OpenFolder() => DialogService.OpenFolderAsync(Job.Output.Path);
}
