using Enlist.Runner.Protocol;

namespace Enlist.Runner.Tests;

/// <summary>
/// The paths a service or job takes when it fails in a way the runner has to REPORT rather than
/// recover from. All three pinned here share a shape: the runner announced a transition, then hit a
/// failure, and then said nothing further — so the agent, which tracks state from
/// StateChangedMessage and only logs a FaultedMessage, was left holding a state that could never
/// change again. A service stuck at Starting or Stopping for the life of the runner, or a job run
/// that was announced and never resolved.
///
/// Driven against a plugin built to fail in exactly these ways
/// (tests/Enlist.TestPlugins.Misbehaving), because a failure path tested with a mock is a failure
/// path tested against one's own assumptions.
/// </summary>
public sealed class RunnerFailurePathTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(20);

    private const string ThrowsInConstructor = "Throws In Constructor";
    private const string SlowToStop = "Slow To Stop";
    private const string BehavesNormally = "Behaves Normally";

    [Fact]
    public async Task A_service_that_cannot_be_constructed_reports_Faulted_not_a_permanent_Starting()
    {
        await using var agent = await StubAgent.StartAsync(RepoPaths.MisbehavingPluginsOutputDir(), Step);
        await ReceiveAsync<ReadyMessage>(agent);

        await agent.Channel.SendAsync(new StartServiceCommand(ThrowsInConstructor));

        var states = new List<RunnerState>();
        FaultedMessage? fault = null;

        // Both, not either: the state change now goes out FIRST (that is the fix), so stopping at it
        // would read back only what the test itself caused to be sent second.
        using var cts = new CancellationTokenSource(Step);
        while (states.LastOrDefault() != RunnerState.Faulted || fault is null)
        {
            var message = await agent.Channel.ReceiveAsync(cts.Token)
                ?? throw new InvalidOperationException("the channel closed before the service reported a terminal state.");

            switch (message)
            {
                case StateChangedMessage { TargetKind: RunnerTargetKind.Service } sc when sc.Target == ThrowsInConstructor:
                    states.Add(sc.State);
                    break;

                case FaultedMessage f when f.Target == ThrowsInConstructor:
                    fault = f;
                    break;
            }
        }

        // Starting, then Faulted. The fault alone was what this used to send, and a FaultedMessage
        // does not move the agent's idea of the state - only a StateChangedMessage does.
        Assert.Equal([RunnerState.Starting, RunnerState.Faulted], states);
        Assert.NotNull(fault);
        Assert.Contains("cannot be constructed", fault!.Error);

        // One broken service is not a broken application: its neighbour still starts.
        await agent.Channel.SendAsync(new StartServiceCommand(BehavesNormally));
        var running = await ReceiveUntilAsync<StateChangedMessage>(
            agent, m => m.Target == BehavesNormally && m.State == RunnerState.Running);
        Assert.Equal(RunnerState.Running, running.State);

        await agent.Channel.SendAsync(new ShutdownCommand(5000));
    }

    [Fact]
    public async Task A_stop_that_overruns_its_timeout_still_reports_Stopped_when_it_finishes()
    {
        await using var agent = await StubAgent.StartAsync(RepoPaths.MisbehavingPluginsOutputDir(), Step);
        await ReceiveAsync<ReadyMessage>(agent);

        await agent.Channel.SendAsync(new StartServiceCommand(SlowToStop));
        await ReceiveUntilAsync<StateChangedMessage>(agent, m => m.Target == SlowToStop && m.State == RunnerState.Running);

        // Far shorter than the service's own stop, so the timeout is certain to elapse first. The
        // runner gives up waiting, says Stopping a second time, and removes the registry entry - and
        // used to drop the still-running invocation on the floor there. Nothing else was coming to
        // correct the state: a lone StopServiceCommand does not make the agent kill the process, and
        // a second Stop is answered "not running - ignored" because the entry is already gone. The
        // service sat at Stopping for the life of the runner.
        await agent.Channel.SendAsync(new StopServiceCommand(SlowToStop, TimeoutMs: 250));

        var states = new List<RunnerState>();
        using var cts = new CancellationTokenSource(Step);
        while (states.LastOrDefault() != RunnerState.Stopped)
        {
            var message = await agent.Channel.ReceiveAsync(cts.Token)
                ?? throw new InvalidOperationException("the channel closed before the overrunning stop reported Stopped.");

            if (message is StateChangedMessage { TargetKind: RunnerTargetKind.Service } sc && sc.Target == SlowToStop)
            {
                states.Add(sc.State);
            }
        }

        // Stopping, Stopping again when the timeout elapsed, then Stopped when the plugin finally
        // returned. The second Stopping is the advisory one and is expected; the Stopped at the end
        // is the whole point.
        Assert.Equal(RunnerState.Stopped, states[^1]);
        Assert.True(states.Count(s => s == RunnerState.Stopping) >= 2, $"expected the advisory second Stopping, got [{string.Join(", ", states)}]");

        await agent.Channel.SendAsync(new ShutdownCommand(5000));
    }

    [Fact]
    public async Task A_job_command_with_no_settings_at_all_still_reports_a_result()
    {
        await using var agent = await StubAgent.StartAsync(RepoPaths.MisbehavingPluginsOutputDir(), Step);
        await ReceiveAsync<ReadyMessage>(agent);

        // RunJobCommand.Settings is declared non-nullable and arrives null anyway whenever the
        // command's JSON omits "settings" - which is what any caller that is not this repo's own
        // agent will do. Merging then threw a NullReferenceException AFTER Starting had gone out and
        // BEFORE the try that turns a job failure into a JobResultMessage, so the run was announced
        // and never resolved, and the agent's in-flight entry for it was never cleared.
        var runId = Guid.NewGuid().ToString("N");
        await agent.Channel.SendAsync(new RunJobCommand("Echo Settings", runId, null!));

        var result = await ReceiveUntilAsync<JobResultMessage>(agent, m => m.RunId == runId);

        Assert.Equal(JobOutcome.Succeeded, result.Outcome);

        await agent.Channel.SendAsync(new ShutdownCommand(5000));
    }

    /// <summary>The first message of a given type, skipping anything that arrives ahead of it.</summary>
    private static Task<T> ReceiveAsync<T>(StubAgent agent) where T : RunnerMessage =>
        ReceiveUntilAsync<T>(agent, _ => true);

    private static async Task<T> ReceiveUntilAsync<T>(StubAgent agent, Func<T, bool> predicate) where T : RunnerMessage
    {
        using var cts = new CancellationTokenSource(Step);
        while (true)
        {
            var message = await agent.Channel.ReceiveAsync(cts.Token)
                ?? throw new InvalidOperationException($"the channel closed before a matching {typeof(T).Name} arrived.");

            if (message is T match && predicate(match))
            {
                return match;
            }
        }
    }
}
