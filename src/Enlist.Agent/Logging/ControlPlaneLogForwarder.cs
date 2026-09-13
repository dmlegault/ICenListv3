using System.Collections.Concurrent;
using System.Net.Http.Json;

using Enlist.ControlPlane.Contracts;
using Enlist.Runner.Protocol;

namespace Enlist.Agent.Logging;

/// <summary>
/// Mirrors app log lines to the control plane on a timer, batched — one HTTP POST per flush interval
/// rather than per line, same reasoning as AgentFileLogSink's own append-per-write already accepts for
/// the local file (a log line is not worth its own round trip). The local file remains the durable
/// copy; this is a best-effort near-real-time mirror purely for the portal's log-tail page, so a queue
/// capped at MaxQueueLength drops the OLDEST buffered lines under sustained control-plane unavailability
/// rather than growing without bound — losing some forwarded history is an acceptable trade the local
/// file doesn't share, since IT never drops anything queued this way.
/// </summary>
public sealed class ControlPlaneLogForwarder : ILogForwarder, IAsyncDisposable, IReportsDiagnostics
{
    private const int MaxQueueLength = 2000;

    // The first failure, one line per five minutes while it lasts, and the recovery — via AgentHost to
    // the agent log. A forwarder whose every batch was refused for an hour used to look exactly like one
    // that was delivering.
    private readonly FailureNotice _notice = new("Forwarding log lines to the control plane", TimeSpan.FromMinutes(5));

    public event Action<string>? Diagnostic;

    private readonly HttpClient _http;
    private readonly string _agentName;
    private readonly TimeSpan _flushInterval;
    private readonly ConcurrentQueue<LogEntrySubmission> _queue = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _flushLoop;

    public ControlPlaneLogForwarder(HttpClient http, string agentName, TimeSpan? flushInterval = null)
    {
        _http = http;
        _agentName = agentName;
        _flushInterval = flushInterval ?? TimeSpan.FromSeconds(2);
        _flushLoop = RunFlushLoopAsync(_shutdownCts.Token);
    }

    public void Enqueue(string applicationName, LogMessage message)
    {
        // Oldest out first when full. Count is a snapshot, so under contention this can shed a line
        // more than strictly necessary — the cap bounds memory; it is not an accounting.
        while (_queue.Count >= MaxQueueLength && _queue.TryDequeue(out _))
        {
        }

        _queue.Enqueue(new LogEntrySubmission(applicationName, message.Level.ToString(), message.Source, message.Text, message.TimestampUtc));
    }

    private async Task RunFlushLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_flushInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await FlushAsync(ct).ConfigureAwait(false);
        }

        // Final best-effort flush on shutdown — whatever's left over goes out once rather than being
        // silently discarded, though the local file already has it regardless.
        await FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        var batch = new List<LogEntrySubmission>();
        while (_queue.TryDequeue(out var entry))
        {
            batch.Add(entry);
        }

        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            using var response = await _http.PostAsJsonAsync($"/api/agents/{Uri.EscapeDataString(_agentName)}/logs", new SubmitLogEntriesRequest(batch), ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (_notice.Succeeded() is { } recovered)
            {
                Diagnostic?.Invoke(recovered);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down — not a failure of the control plane.
        }
        catch (Exception ex)
        {
            // Best-effort, same posture as ControlPlaneStatusReporter — a failed forward must never
            // take the agent down. The batch is simply dropped rather than requeued, matching this
            // class's own "some loss under sustained outage is fine" posture above; the drop is said.
            if (_notice.Failed($"{TransportFailure.Describe(ex)} ({batch.Count} line(s) dropped)") is { } line)
            {
                Diagnostic?.Invoke(line);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdownCts.Cancel();
        try
        {
            await _flushLoop.ConfigureAwait(false);
        }
        catch
        {
        }

        _shutdownCts.Dispose();
    }
}
