using Cronos;

using Enlist.ControlPlane.Contracts;

namespace Enlist.Agent.Scheduling;

/// <summary>
/// Computes fire times and raises them — nothing more. This is the "Why the scheduler lives in the
/// agent" piece from docs/03-architecture/enList-v3-Design.md section 2: the agent may reference a cron library, the
/// runner never can. Cronos does cron MATH only (next-occurrence), not a scheduling engine — the loop
/// that decides when to check is this class's own, deliberately simple rather than pulling in Quartz
/// for what's a one-line calculation.
///
/// Registrations are tracked per (applicationName, jobName) rather than as a flat list — required for
/// crash-restart to be safe: AgentHost re-registers an app's jobs every time it (re)starts that
/// application, and without per-key tracking a restart would leave the OLD loop still running
/// alongside the new one, firing the same job twice on every tick.
/// </summary>
public sealed class JobScheduler : IAsyncDisposable
{
    private readonly Func<string, string, CancellationToken, Task> _onDue;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Dictionary<(string App, string Job), Registration> _registrations = new();

    // Dictionary<TKey,TValue> isn't thread-safe, and callers are no longer guaranteed serial by
    // construction — StartApplicationAsync (driven by AgentHost's reconciliation loop) and
    // ExecuteCommandAsync (driven by an independent SignalR command push, fire-and-forget — see
    // AgentHost) can both call Register/Unregister for the SAME (app, job) key at genuinely the same
    // time. Without this lock, that race can leave two RunLoopAsync loops alive for one key at once —
    // observed as a job firing twice on the same tick right after a disable/enable.
    private readonly object _lock = new();

    private static readonly TimeSpan OneMillisecond = TimeSpan.FromMilliseconds(1);

    private readonly Func<string, Task>? _onDiagnostic;
    private readonly TimeSpan _maxSleep;

    /// <summary>onDiagnostic receives what an operator should see (a loop that stopped, a callback that threw). maxSleep bounds a single Task.Delay — a day by default, which is well under the ~49.7 days Task.Delay refuses; tests shorten it to prove the slicing.</summary>
    public JobScheduler(Func<string, string, CancellationToken, Task> onDue, Func<string, Task>? onDiagnostic = null, TimeSpan? maxSleep = null)
    {
        _onDue = onDue;
        _onDiagnostic = onDiagnostic;
        _maxSleep = maxSleep ?? TimeSpan.FromDays(1);
    }

    /// <summary>
    /// cronExpression is whatever CronExpressions (Contracts) accepts — five fields or six, '?' for '*'
    /// — the same rule the control plane applies before an override is stored, so a job that reaches
    /// this point with an expression that does not parse was declared that way in its own attribute.
    ///
    /// Idempotent: registering the same (applicationName, jobName) again replaces the previous
    /// registration (and cancels its loop) rather than running both side by side.
    /// </summary>
    public void Register(string applicationName, string jobName, string cronExpression)
    {
        CronExpression parsed;
        try
        {
            parsed = CronExpressions.Parse(cronExpression);
        }
        catch (FormatException ex)
        {
            throw new FormatException($"Invalid cron expression for {applicationName}/{jobName}: {ex.Message}", ex);
        }

        lock (_lock)
        {
            var key = (applicationName, jobName);
            UnregisterCore(applicationName, jobName);

            var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
            var loop = RunLoopAsync(applicationName, jobName, parsed, cts.Token);
            _registrations[key] = new Registration(cts, loop);
        }
    }

    public void Unregister(string applicationName, string jobName)
    {
        lock (_lock)
        {
            UnregisterCore(applicationName, jobName);
        }
    }

    /// <summary>Called before re-registering an application's jobs on restart, and on a clean stop — removes every job this application has, regardless of name.</summary>
    public void UnregisterAll(string applicationName)
    {
        lock (_lock)
        {
            foreach (var key in _registrations.Keys.Where(k => k.App == applicationName).ToList())
            {
                UnregisterCore(key.App, key.Job);
            }
        }
    }

    /// <summary>Callers must already hold _lock.</summary>
    private void UnregisterCore(string applicationName, string jobName)
    {
        if (_registrations.Remove((applicationName, jobName), out var registration))
        {
            registration.Cts.Cancel();
            registration.Cts.Dispose();
        }
    }

    private async Task RunLoopAsync(string applicationName, string jobName, CronExpression cron, CancellationToken ct)
    {
        try
        {
            var from = DateTimeOffset.UtcNow;
            while (!ct.IsCancellationRequested)
            {
                var next = cron.GetNextOccurrence(from, TimeZoneInfo.Utc);
                if (next is null)
                {
                    // A well-formed expression that mathematically never fires again (e.g. Feb 30th) —
                    // not an error, just nothing left for this loop to do.
                    return;
                }

                // Task.Delay refuses anything past about 49.7 days, and a yearly cron computes exactly
                // that: the throw used to fault this loop unobserved, and the job never fired again. So
                // the wait is sliced — and never trusted: a timer can wake a fraction of a millisecond
                // early (the delay is truncated to whole milliseconds), and firing on that wake used to
                // fire AGAIN on the next pass for the same occurrence. Only the clock says "due".
                while (true)
                {
                    var remaining = next.Value - DateTimeOffset.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    var slice = remaining < _maxSleep ? remaining : _maxSleep;
                    await Task.Delay(slice < OneMillisecond ? OneMillisecond : slice, ct).ConfigureAwait(false);
                }

                try
                {
                    await _onDue(applicationName, jobName, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // The callback owns reporting its own failures (see AgentHost.RunJobDueAsync); one
                    // that throws anyway is still said here, and still does not stop this job from being
                    // tried again next time.
                    await DiagnosticAsync($"{applicationName}/{jobName}: the due-callback threw and this firing is lost - {ex.Message}").ConfigureAwait(false);
                }

                // One firing per occurrence: the next search starts strictly after this one — or after
                // now, if the run started late, so occurrences missed while the machine slept are not
                // caught up in a burst.
                var now = DateTimeOffset.UtcNow;
                from = now > next.Value ? now : next.Value;
            }
        }
        catch (OperationCanceledException)
        {
            // Unregistered, or shutting down.
        }
        catch (Exception ex)
        {
            await DiagnosticAsync($"{applicationName}/{jobName}: the scheduling loop stopped unexpectedly - the job will not fire again until the application restarts: {ex}").ConfigureAwait(false);
        }
    }

    private async Task DiagnosticAsync(string message)
    {
        try
        {
            if (_onDiagnostic is not null)
            {
                await _onDiagnostic(message).ConfigureAwait(false);
            }
        }
        catch
        {
            // A diagnostic that cannot be written is not worth a second failure.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdownCts.Cancel();

        List<Registration> snapshot;
        lock (_lock)
        {
            snapshot = _registrations.Values.ToList();
            _registrations.Clear();
        }

        try
        {
            await Task.WhenAll(snapshot.Select(r => r.Loop)).ConfigureAwait(false);
        }
        catch
        {
        }

        foreach (var registration in snapshot)
        {
            registration.Cts.Dispose();
        }

        _shutdownCts.Dispose();
    }

    private sealed record Registration(CancellationTokenSource Cts, Task Loop);
}
