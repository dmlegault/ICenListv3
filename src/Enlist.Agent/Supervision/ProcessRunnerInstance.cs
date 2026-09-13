using System.Diagnostics;

using Enlist.Runner.Protocol;

namespace Enlist.Agent.Supervision;

/// <summary>
/// One enlist-runner.exe child process, the control channel it connects over, and the channel wrapping
/// it. The agent creates the server endpoint (the runner dials in as client — see
/// Enlist.Runner/Program.cs), mirroring exactly what Enlist.Runner.Tests' StubAgent already proved out
/// The endpoint is a named pipe by default and can be a Unix domain socket instead; this
/// class holds it only as a Stream and is indifferent to which (see IChannelListener).
///
/// The <see cref="IRunnerInstance"/> implementation for locally-spawned processes; ContainerRunnerInstance
/// is the other. Constructed exclusively by <see cref="ProcessRunnerBackend"/>, which owns the start
/// sequence (transport, process, Job Object, connect-with-timeout) that lives in the backend as a
/// factory. See docs/03-architecture/Container-Story.md §6.2 for why the static had to go.
/// </summary>
public sealed class ProcessRunnerInstance : IRunnerInstance
{
    private readonly TaskCompletionSource<ReadyMessage> _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Process _process;
    private readonly Stream _transport;
    private readonly MessageChannel<AgentCommand, RunnerMessage> _channel;
    private readonly Func<IRunnerInstance, RunnerMessage, Task> _onMessage;
    private readonly Func<string, Task> _writeAgentLog;
    private Task? _receiveLoop;
    /// <summary>0 until DisposeAsync has run - see DisposeAsync.</summary>
    private int _disposed;

    private volatile bool _stopRequested;

    public string ApplicationName { get; }

    /// <inheritdoc />
    public event Action<IRunnerInstance, bool>? Exited;

    /// <inheritdoc />
    public Task<ReadyMessage> WhenReady => _readyTcs.Task;

    /// <summary>
    /// Captured once at construction rather than read from the Process object on demand: Process
    /// throws once Dispose() has run (see DisposeAsync), but the OS process id itself stays a valid,
    /// harmless thing to have logged or checked against — e.g. Process.GetProcessById(pid) — even
    /// after this wrapper is gone.
    /// </summary>
    public int Pid { get; }

    /// <inheritdoc />
    public string RuntimeId => Pid.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public bool HasExited => _process.HasExited;

    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    internal ProcessRunnerInstance(
        string applicationName,
        Process process,
        Stream transport,
        MessageChannel<AgentCommand, RunnerMessage> channel,
        Func<IRunnerInstance, RunnerMessage, Task> onMessage,
        Func<string, Task> writeAgentLog)
    {
        ApplicationName = applicationName;
        Pid = process.Id;
        _process = process;
        _transport = transport;
        _channel = channel;
        _onMessage = onMessage;
        _writeAgentLog = writeAgentLog;

        // Setting EnableRaisingEvents on an already-exited process still raises Exited (the runtime
        // checks and fires immediately on subscribe) — so there's no race even though this runs after
        // the backend's connect wait, not immediately after Process.Start.
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => Exited?.Invoke(this, _stopRequested);
    }

    /// <summary>
    /// Starts pumping messages off the channel. Separate from the constructor, and called by the
    /// backend immediately after it, purely so the loop can't observe a half-constructed instance.
    /// </summary>
    internal void StartReceiving() => _receiveLoop = ReceiveLoopAsync();

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
                // The channel itself failed, so nothing more will ever arrive. Expected when the process
                // has just died — Exited reports that on its own — but worth a line when it has NOT,
                // because from here on the agent can no longer hear a runner that is still running.
                _readyTcs.TrySetException(ex);
                if (!HasExitedQuietly())
                {
                    await _writeAgentLog(
                        $"{ApplicationName}: control channel failed while the runner is still running - " +
                        $"no further messages from it will be processed: {ex.Message}").ConfigureAwait(false);
                }

                return;
            }

            if (message is null)
            {
                // The runner closed its end. If Ready never arrived, WhenReady needs to fault rather
                // than hang forever — a runner that dies before discovery finishes is a real failure
                // the caller must observe, not silence.
                _readyTcs.TrySetException(new InvalidOperationException(
                    $"enlist-runner for '{ApplicationName}' closed its control channel before completing discovery."));
                return;
            }

            if (message is ReadyMessage ready)
            {
                _readyTcs.TrySetResult(ready);
            }

            // Caught per message, never around the loop. A handler faulting on ONE message used to end
            // the loop: the runner kept running, and every log line, state change and job result after
            // that was lost while the application still reported Running. Nothing above this loop can
            // recover it, so the fault is recorded and the next message is read.
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

    /// <summary>Process.HasExited throws once the Process is disposed; in that state the answer is "yes".</summary>
    private bool HasExitedQuietly()
    {
        try
        {
            return _process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Sends Shutdown, waits up to gracePeriod for the process to exit on its own, then kills it.
    /// This IS the escalation path design doc section 3 promises — "the one capability that justified
    /// Aware's C++ host is obtained here by process topology alone" — and it belongs to the agent, not
    /// the runner: the runner's own StopServiceCommand.TimeoutMs is advisory only, this is the real
    /// enforcement.
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
            // Channel already gone — nothing left to ask nicely, fall through to waiting/killing.
        }

        var exitedOnItsOwn = await WaitForExitAsync(gracePeriod).ConfigureAwait(false);
        if (!exitedOnItsOwn)
        {
            ProcessKill.Quietly(_process);
        }

        return exitedOnItsOwn;
    }

    /// <summary>Not on IRunnerInstance — AgentHost never calls it, only StopAsync above does.</summary>
    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Bounds the drain below. A receive loop that somehow still doesn't finish must not hang disposal forever — losing a few trailing log lines from a process that is already being torn down is strictly better than never returning.</summary>
    private static readonly TimeSpan ReceiveLoopDrainTimeout = TimeSpan.FromSeconds(5);

    public async ValueTask DisposeAsync()
    {
        // Idempotent, for the same reason ContainerRunnerInstance is: disposal is reachable from an
        // orderly stop, a crash-exit handler and AgentHost's shutdown loop, and the second pass must
        // not throw at whoever is tidying up.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // Defensive: normal AgentHost flow always calls StopAsync first, but a caller that disposes
        // directly (an early-failure path, a test) shouldn't have the kill below misreported as a crash.
        _stopRequested = true;

        // Kill BEFORE draining the receive loop, never after. The loop sits in ReceiveAsync until the
        // far end closes, so awaiting it first deadlocks against any runner that is still alive and
        // healthy — and two callers dispose without stopping first, precisely when the runner may be
        // exactly that: AgentHost's discovery-timeout path (a runner hung before sending Ready is alive,
        // just not talking) and its protocol-mismatch path (the runner is perfectly healthy, it just
        // speaks the wrong version). Killing first makes the far end close, which is what lets the loop
        // observe EOF and finish — disposing a HEALTHY runner would otherwise hang forever.
        ProcessKill.Quietly(_process);

        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.WaitAsync(ReceiveLoopDrainTimeout).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        await _channel.DisposeAsync().ConfigureAwait(false);
        _transport.Dispose();
        _process.Dispose();
    }
}
