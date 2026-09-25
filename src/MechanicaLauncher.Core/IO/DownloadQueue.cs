using System.Diagnostics;
using MechanicaLauncher.Core.Game;

namespace MechanicaLauncher.Core.IO;

public enum DownloadState { Queued, Running, Completed, Cancelled, Failed }
public sealed record DownloadFileSnapshot(string Name, long Received, long Total, bool Complete);
public sealed record DownloadSnapshot(Guid Id, string Title, string? InstanceId, DownloadState State, string? Error,
    long Received, long Total, double BytesPerSecond, IReadOnlyList<DownloadFileSnapshot> Files, bool CanRetry);

public sealed class DownloadJob
{
    private readonly object _sync = new();
    private readonly Dictionary<string, DownloadFileSnapshot> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch _clock = new();
    private long _traffic;
    private long _sampleTraffic;
    private double _sampleTime;
    private double _speed;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Func<CancellationToken, Task>? Work { get; set; }
    internal Action? RetryAction { get; set; }
    internal Action? Changed { get; init; }
    internal CancellationTokenSource Cancellation { get; init; } = new();
    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; init; } = "";
    public string? InstanceId { get; init; }
    public string? ResultInstanceId { get; set; }
    public DownloadState State { get { lock (_sync) return _state; } }
    private DownloadState _state = DownloadState.Queued;
    private string? _error;
    private bool _retried;
    public Task Completion => _completion.Task;
    public bool IsCancellationRequested => Cancellation.IsCancellationRequested;
    internal bool MarkRetried()
    {
        lock (_sync)
        {
            if (_retried || _state is not (DownloadState.Failed or DownloadState.Cancelled)) return false;
            _retried = true;
            return true;
        }
    }

    internal bool Start()
    {
        lock (_sync)
        {
            if (_state != DownloadState.Queued || Cancellation.IsCancellationRequested) return false;
            _state = DownloadState.Running;
            _clock.Start();
            return true;
        }
    }
    public void Complete() => Finish(DownloadState.Completed, null);
    public void Fail(Exception error) => Finish(DownloadState.Failed, CrashAnalyzer.Redact(error.Message));
    internal void Finish(DownloadState state, string? error)
    {
        lock (_sync)
        {
            if (_state is DownloadState.Completed or DownloadState.Cancelled or DownloadState.Failed) return;
            _state = state;
            _error = error;
            if (state == DownloadState.Completed) { Work = null; RetryAction = null; }
            _clock.Stop();
        }
        _completion.TrySetResult();
        Changed?.Invoke();
    }
    public void Cancel()
    {
        lock (_sync)
        {
            if (_state is not (DownloadState.Queued or DownloadState.Running)) return;
            Cancellation.Cancel();
            if (_state == DownloadState.Queued) Finish(DownloadState.Cancelled, null);
        }
    }
    internal void Progress(string path, long received, long total, int bytes, bool complete = false)
    {
        lock (_sync)
        {
            _files[path] = new(Path.GetFileName(path), received, total, complete);
            _traffic += bytes;
        }
    }
    public DownloadSnapshot Snapshot()
    {
        lock (_sync)
        {
            var elapsed = _clock.Elapsed.TotalSeconds;
            if (elapsed - _sampleTime >= .5)
            {
                _speed = (_traffic - _sampleTraffic) / (elapsed - _sampleTime);
                _sampleTraffic = _traffic;
                _sampleTime = elapsed;
            }
            var files = _files.Values.ToArray();
            return new(Id, Title, ResultInstanceId ?? InstanceId, _state, _error, files.Sum(f => f.Received),
                files.Any(f => f.Total <= 0) ? 0 : files.Sum(f => f.Total),
                _state == DownloadState.Running ? _speed : 0, files,
                !_retried && _state is DownloadState.Failed or DownloadState.Cancelled && (Work != null || RetryAction != null));
        }
    }
}

public sealed class DownloadQueue
{
    private readonly object _sync = new();
    private readonly List<DownloadJob> _jobs = [];
    private readonly Queue<DownloadJob> _pending = new();
    private bool _pumping;
    private static readonly AsyncLocal<DownloadJob?> CurrentJob = new();
    public static DownloadJob? Current => CurrentJob.Value;
    public static CancellationTokenSource? CurrentCancellation => CurrentJob.Value?.Cancellation;
    public event Action? Changed;

    public IReadOnlyList<DownloadJob> Jobs { get { lock (_sync) return _jobs.ToArray(); } }
    public bool HasPending => Jobs.Any(j => j.State is DownloadState.Queued or DownloadState.Running);

    public DownloadJob Enqueue(string title, string? instanceId, Func<CancellationToken, Task> work)
    {
        var job = new DownloadJob { Title = title, InstanceId = instanceId, Work = work, Changed = () => Changed?.Invoke() };
        bool start;
        lock (_sync)
        {
            _jobs.Add(job);
            _pending.Enqueue(job);
            start = !_pumping;
            _pumping = true;
        }
        Changed?.Invoke();
        if (start) _ = PumpAsync();
        return job;
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            DownloadJob job;
            lock (_sync)
            {
                if (!_pending.TryDequeue(out job!)) { _pumping = false; return; }
            }
            if (!job.Start()) continue;
            var previous = CurrentJob.Value;
            CurrentJob.Value = job;
            try
            {
                await job.Work!(job.Cancellation.Token);
                job.Complete();
            }
            catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested) { job.Finish(DownloadState.Cancelled, null); }
            catch (Exception ex) { job.Fail(ex); }
            finally { CurrentJob.Value = previous; }
        }
    }

    public TrackingScope Track(string title, string? instanceId, CancellationTokenSource cancellation, Action? retry = null)
    {
        var job = new DownloadJob { Title = title, InstanceId = instanceId, Cancellation = cancellation, RetryAction = retry, Changed = () => Changed?.Invoke() };
        lock (_sync) _jobs.Add(job);
        job.Start();
        Changed?.Invoke();
        var previous = CurrentJob.Value;
        CurrentJob.Value = job;
        return new(job, previous);
    }

    public void Retry(DownloadJob job)
    {
        if (!job.Snapshot().CanRetry) return;
        if (job.Work != null) { if (job.MarkRetried()) Enqueue(job.Title, job.InstanceId, job.Work); }
        else job.RetryAction?.Invoke();
    }

    public void CancelAll()
    {
        foreach (var job in Jobs) job.Cancel();
    }

    public sealed class TrackingScope : IDisposable
    {
        private readonly DownloadJob? _previous;
        internal TrackingScope(DownloadJob job, DownloadJob? previous) { Job = job; _previous = previous; }
        public DownloadJob Job { get; }
        public void Dispose()
        {
            CurrentJob.Value = _previous;
            if (Job.State == DownloadState.Running)
                Job.Finish(Job.Cancellation.IsCancellationRequested ? DownloadState.Cancelled : DownloadState.Failed, null);
        }
    }
}
