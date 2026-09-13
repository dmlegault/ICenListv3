using Enlist.ControlPlane.Contracts;
using Enlist.Runner.Protocol;

namespace Enlist.Agent.Supervision;

/// <summary>
/// One runner hosted in a container, as AgentHost sees it — the second <see cref="IRunnerInstance"/>
/// implementation, and the one C0's seam existed for.
///
/// Structurally the mirror of <see cref="ProcessRunnerInstance"/>: same receive loop, same WhenReady
/// contract, same Exited semantics. Only three things differ (docs/03-architecture/Container-Story.md
/// §6.2) — execution lifecycle is engine calls instead of Process, transport establishment is a TCP
/// dial instead of a pipe accept, and containment differs — though NOT in the direction first
/// assumed. A container bounds what runs INSIDE it, but unlike a Job Object it does not tie its own
/// lifetime to the agent's, so a killed agent leaves it running. That is why ContainerRunnerBackend
/// has to reap orphans explicitly and the process backend does not.
/// </summary>
public sealed class ContainerRunnerInstance : IRunnerInstance
{
    private readonly TaskCompletionSource<ReadyMessage> _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IContainerEngine _engine;
    private readonly string _containerId;
    private readonly Stream _transport;
    private readonly MessageChannel<AgentCommand, RunnerMessage> _channel;
    private readonly Func<IRunnerInstance, RunnerMessage, Task> _onMessage;
    private readonly Func<string, Task> _writeAgentLog;
    private readonly CancellationTokenSource _watchCts = new();
    private readonly IReadOnlyList<ResolvedEndpointDto> _endpoints;
    private Task? _receiveLoop;
    private Task? _exitWatch;
    /// <summary>0 until DisposeAsync has run. Interlocked rather than volatile: two concurrent disposals must have exactly one winner, not merely see each other eventually.</summary>
    private int _disposed;

    private volatile bool _stopRequested;
    private volatile bool _hasExited;
    private volatile int _exitCode;

    public string ApplicationName { get; }

    /// <inheritdoc />
    public event Action<IRunnerInstance, bool>? Exited;

    /// <inheritdoc />
    public Task<ReadyMessage> WhenReady => _readyTcs.Task;

    /// <summary>Always 0 — see IRunnerInstance.Pid. A container's host pid is meaningless on this platform, and RuntimeId carries the identity that is actually actionable.</summary>
    public int Pid => 0;

    /// <inheritdoc />
    public string RuntimeId => _containerId;

    /// <inheritdoc />
    public IReadOnlyList<ResolvedEndpointDto> Endpoints => _endpoints;

    public bool HasExited => _hasExited;

    public int? ExitCode => _hasExited ? _exitCode : null;

    internal ContainerRunnerInstance(
        string applicationName,
        IContainerEngine engine,
        string containerId,
        Stream transport,
        MessageChannel<AgentCommand, RunnerMessage> channel,
        Func<IRunnerInstance, RunnerMessage, Task> onMessage,
        Func<string, Task> writeAgentLog,
        IReadOnlyList<ResolvedEndpointDto> endpoints)
    {
        ApplicationName = applicationName;
        _endpoints = endpoints;
        _engine = engine;
        _containerId = containerId;
        _transport = transport;
        _channel = channel;
        _onMessage = onMessage;
        _writeAgentLog = writeAgentLog;
    }

    /// <summary>
    /// Starts the receive loop and the exit watch. Separate from the constructor, and called by the
    /// backend immediately after it, so neither can observe a half-constructed instance.
    /// </summary>
    internal void StartPumps()
    {
        _receiveLoop = ReceiveLoopAsync();
        _exitWatch = WatchForExitAsync();
    }

    /// <summary>
    /// The container's stand-in for Process.Exited. `docker wait` blocks until the container exits, so
    /// this is one long-lived call rather than a poll — and it is what makes crash-restart identical
    /// for both backends: AgentHost only ever sees the Exited event and the wasRequested flag, exactly
    /// as it does for a process.
    /// </summary>
    private async Task WatchForExitAsync()
    {
        try
        {
            var exitCode = await _engine.WaitForExitAsync(_containerId, _watchCts.Token).ConfigureAwait(false);
            _exitCode = exitCode;
            _hasExited = true;
            Exited?.Invoke(this, _stopRequested);
        }
        catch (OperationCanceledException)
        {
            // Disposal cancelled the watch — no exit to report that the caller doesn't already know about.
        }
        catch
        {
            // The engine became unreachable (daemon restarted, container removed out from under us).
            // Treat that as an exit: an instance whose container can no longer be observed is not
            // running, and reporting nothing would leave AgentHost believing it still is.
            _hasExited = true;
            Exited?.Invoke(this, _stopRequested);
        }
    }

    public Task SendAsync(AgentCommand command, CancellationToken ct = default) => _channel.SendAsync(command, ct);

    private async Task ReceiveLoopAsync()
    {
        while (true)
        {
            RunnerMessage? message;
            try
            {
                message = await _channel.ReceiveAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The channel itself failed, so nothing more will ever arrive. Expected when the
                // container has just exited — the exit watch reports that — but worth a line when it
                // has NOT, because from here on the agent can no longer hear a runner that is still running.
                _readyTcs.TrySetException(ex);
                if (!_hasExited)
                {
                    await _writeAgentLog(
                        $"{ApplicationName}: control channel failed while the container is still running - " +
                        $"no further messages from it will be processed: {ex.Message}").ConfigureAwait(false);
                }

                return;
            }

            if (message is null)
            {
                _readyTcs.TrySetException(new InvalidOperationException(
                    $"enlist-runner container for '{ApplicationName}' closed its control channel before completing discovery."));
                return;
            }

            if (message is ReadyMessage ready)
            {
                _readyTcs.TrySetResult(ready);
            }

            // Caught per message, never around the loop — see ProcessRunnerInstance for why: a handler
            // faulting on ONE message must not end the agent's ability to hear this runner.
            try
            {
                await _onMessage(this, message).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _writeAgentLog(
                    $"{ApplicationName}: error handling a {message.GetType().Name} from its runner - " +
                    $"the message was dropped, later ones are still processed: {ex}").ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Asks the container to stop, escalating to a kill after the grace period. The engine performs the
    /// escalation itself (SIGTERM, wait, SIGKILL), which is the same sequence ProcessRunnerInstance
    /// performs by hand — so the return value means the same thing in both: true if it left on its own.
    /// </summary>
    public async Task<bool> StopAsync(TimeSpan gracePeriod)
    {
        _stopRequested = true;

        try
        {
            await SendAsync(new ShutdownCommand((int)gracePeriod.TotalMilliseconds)).ConfigureAwait(false);
        }
        catch
        {
            // Channel already gone — nothing left to ask nicely, let the engine escalate.
        }

        return await _engine.StopAsync(_containerId, gracePeriod).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        // Idempotent, because disposal is reachable from more than one direction: an orderly stop, a
        // crash-exit handler, and AgentHost's own shutdown loop. A second pass used to call
        // CancelAsync on the already-disposed _watchCts and throw ObjectDisposedException at whoever
        // was tidying up.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _stopRequested = true;

        // Same ordering lesson as ProcessRunnerInstance: tear the far end down BEFORE draining the
        // receive loop, or disposing a healthy runner deadlocks against a loop that only ends when the
        // channel closes. Removing the container is what closes it here.
        await _engine.RemoveAsync(_containerId).ConfigureAwait(false);

        await _watchCts.CancelAsync().ConfigureAwait(false);

        foreach (var pump in new[] { _receiveLoop, _exitWatch })
        {
            if (pump is null)
            {
                continue;
            }

            try
            {
                await pump.WaitAsync(PumpDrainTimeout).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        await _channel.DisposeAsync().ConfigureAwait(false);
        _transport.Dispose();
        _watchCts.Dispose();
    }

    private static readonly TimeSpan PumpDrainTimeout = TimeSpan.FromSeconds(5);
}
