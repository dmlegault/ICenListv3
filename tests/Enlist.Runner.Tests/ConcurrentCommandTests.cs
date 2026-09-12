using Enlist.Runner.Protocol;

namespace Enlist.Runner.Tests;

/// <summary>
/// Commands arriving faster than the runner can act on them — which is the normal case, not an edge:
/// the agent sends one StartServiceCommand per discovered service back-to-back the moment discovery
/// completes, and an operator can click Stop then Start on one service inside a second.
///
/// RunnerHost dispatches each command on its own thread-pool task (so a synchronous plugin method
/// cannot block the read loop), which means every command handler runs genuinely in parallel with
/// every other. This class pins the two properties that has to hold under: shared state survives
/// parallel writes, and commands for the SAME service are applied in the order they arrived.
/// </summary>
public sealed class ConcurrentCommandTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Regression guard for a real defect: with parallel dispatch, four simultaneous
    /// StartServiceCommands wrote a plain Dictionary from four threads. The losing service failed with
    /// "concurrent update … corrupted its state" BEFORE its [EnlistStart] was invoked, so it was never
    /// started, stayed Starting forever, and was never retried. The demo fleet's own logs showed it a
    /// dozen times. Repeated because a race that reproduces on most starts is not guaranteed to on one.
    /// </summary>
    [Fact]
    public async Task Starting_every_service_at_once_starts_every_service()
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await using var agent = await StubAgent.StartAsync(RepoPaths.OrderProcessorOutputDir(), Step);
            var ready = await Receive<ReadyMessage>(agent);
            Assert.True(ready.Services.Count >= 4, "this test needs a sample with several services to race");

            // Exactly how AgentHost.StartApplicationAsync sends them: one after another, no waiting.
            foreach (var service in ready.Services)
            {
                await agent.Channel.SendAsync(new StartServiceCommand(service.Name));
            }

            var running = new HashSet<string>();
            using var cts = new CancellationTokenSource(Step);
            while (running.Count < ready.Services.Count)
            {
                var message = await agent.Channel.ReceiveAsync(cts.Token)
                    ?? throw new InvalidOperationException("channel closed before every service reported Running");

                switch (message)
                {
                    case StateChangedMessage { TargetKind: RunnerTargetKind.Service, State: RunnerState.Running } sc:
                        running.Add(sc.Target);
                        break;

                    // The defect's exact signature — fail on it directly rather than waiting for the
                    // missing Running to time out, so the failure names the cause.
                    case LogMessage { Source: "runner", Level: LogLevel.Error } log when log.Text.Contains("Unhandled error dispatching"):
                        Assert.Fail($"attempt {attempt}: {log.Text}");
                        break;

                    case FaultedMessage f:
                        Assert.Fail($"attempt {attempt}: service '{f.Target}' faulted: {f.Error}");
                        break;
                }
            }

            await agent.Channel.SendAsync(new ShutdownCommand(5000));
            Assert.True(await DrainUntilExitAsync(agent), $"attempt {attempt}: runner did not exit after shutdown");
            Assert.Equal(0, agent.ExitCode);
        }
    }

    /// <summary>
    /// Two commands for ONE service must be applied in arrival order even though each runs on its
    /// own task. Without that, a Stop can begin while the Start it follows is still executing, and the
    /// service ends up reported Running by a Start that finished after the Stop — a stopped service
    /// with a live-looking state.
    ///
    /// This test found that defect for real on 2026-09-12, and then showed why one pass is not
    /// enough: a semaphore per service granted mutual exclusion but not ORDER, the two dispatch tasks
    /// raced to reach it, and the Stop won roughly one run in four. For months the rest of the time
    /// it passed, so the suite reported green while the ordering guarantee it exists to prove was
    /// unverified. Repeated now, for the same reason its sibling above is.
    /// </summary>
    [Fact]
    public async Task Start_then_stop_for_one_service_are_applied_in_order()
    {
        await using var agent = await StubAgent.StartAsync(RepoPaths.OrderProcessorOutputDir(), Step);
        await Receive<ReadyMessage>(agent);

        const string service = "Order Intake Listener";

        // Twelve cycles, not one: at the observed one-in-four this is the difference between a guard
        // that catches an ordering regression 25% of the time and one that catches it about 97%.
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            await agent.Channel.SendAsync(new StartServiceCommand(service));
            await agent.Channel.SendAsync(new StopServiceCommand(service, TimeoutMs: 5000));

            var states = new List<RunnerState>();
            using var cts = new CancellationTokenSource(Step);
            while (states.LastOrDefault() != RunnerState.Stopped)
            {
                var message = await agent.Channel.ReceiveAsync(cts.Token)
                    ?? throw new InvalidOperationException($"attempt {attempt}: channel closed before the service reported Stopped");

                switch (message)
                {
                    case StateChangedMessage { TargetKind: RunnerTargetKind.Service } sc when sc.Target == service:
                        states.Add(sc.State);
                        break;

                    // The defect's exact signature: the Stop overtook the Start, found nothing in
                    // _runningServices and returned silently, so no terminal state was ever sent and
                    // the loop above had nothing to wait for. Fail on it by name rather than blocking
                    // for the full step and reporting only "the operation was canceled".
                    case LogMessage { Level: LogLevel.Warning } log when log.Text.Contains("not running - ignored"):
                        Assert.Fail($"attempt {attempt}: the Stop was applied before the Start it followed - '{log.Text}'");
                        break;

                    case FaultedMessage f:
                        Assert.Fail($"attempt {attempt}: service '{f.Target}' faulted: {f.Error}");
                        break;
                }
            }

            Assert.Equal([RunnerState.Starting, RunnerState.Running, RunnerState.Stopping, RunnerState.Stopped], states);
        }

        await agent.Channel.SendAsync(new ShutdownCommand(5000));
        Assert.True(await DrainUntilExitAsync(agent));
    }

    private static async Task<bool> DrainUntilExitAsync(StubAgent agent)
    {
        var drain = Task.Run(async () =>
        {
            try
            {
                while (await agent.Channel.ReceiveAsync(CancellationToken.None).ConfigureAwait(false) is not null)
                {
                }
            }
            catch
            {
                // The pipe going away as the runner exits is the expected end of this loop.
            }
        });

        var exited = await agent.WaitForExitAsync(Step).ConfigureAwait(false);
        await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        return exited;
    }

    private static async Task<T> Receive<T>(StubAgent agent) where T : RunnerMessage
    {
        using var cts = new CancellationTokenSource(Step);
        while (true)
        {
            var message = await agent.Channel.ReceiveAsync(cts.Token)
                ?? throw new InvalidOperationException($"channel closed while waiting for {typeof(T).Name}");
            if (message is T typed)
            {
                return typed;
            }
        }
    }
}
