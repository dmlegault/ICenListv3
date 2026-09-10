namespace Enlist.Runner.Legacy.Execution;

/// <summary>
/// One in-flight job run, kept only for as long as its [EnlistExecute] is actually executing.
///
/// It exists so a run can be stopped after it has started. The CancellationToken bound into a job's
/// parameters is worth nothing to whoever wants the run to stop unless something outside the run
/// still holds the source that trips it — this is that something.
///
/// Keyed by RunId rather than job name: nothing prevents two runs of the same job overlapping (a cron
/// tick landing on a still-running manual run, say), and keying by name would make the second run
/// evict the first, after which the first's cleanup would remove the second's entry.
/// </summary>
internal sealed class RunningJobState
{
    private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
    private readonly TaskCompletionSource<bool> _completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public RunningJobState(string jobName, string runId)
    {
        JobName = jobName;
        RunId = runId;
    }

    public string JobName { get; }

    public string RunId { get; }

    /// <summary>Handed to the job's [EnlistExecute] through ParameterBinder, and tripped by Cancel.</summary>
    public CancellationToken Token => _cancellation.Token;

    public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

    /// <summary>Completes once the run has finished — cancelled, faulted or clean — so shutdown can wait for it.</summary>
    public Task Completion => _completed.Task;

    /// <summary>
    /// Safe against the run finishing at the same moment: a cancel that loses that race is a no-op
    /// rather than an ObjectDisposedException on a background dispatch thread.
    /// </summary>
    public void Cancel()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Complete()
    {
        _completed.TrySetResult(true);

        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _cancellation.Dispose();
        }
    }
}
