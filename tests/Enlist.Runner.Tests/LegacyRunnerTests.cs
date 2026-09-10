using System.Diagnostics;

using Enlist.Runner.Protocol;

namespace Enlist.Runner.Tests;

/// <summary>
/// The real net472 runner — `Enlist.Runner.Legacy`'s own `enlist-runner.exe`, on the .NET Framework —
/// driven through the same <see cref="StubAgent"/> and the same protocol as the modern runner, against
/// the real net472 sample. Until this class existed nothing ran that binary: the flavor-routing test
/// registers the modern build under the net472 name and says so, and every legacy-only defect so far
/// (a protocol version that drifted, load failures that were swallowed, a dispatch race that reached it
/// unnoticed) lived in that untested half. The two runners share no assembly by contract; this is the
/// discipline that makes "same contract" a checked claim rather than a stated one.
/// </summary>
public sealed class LegacyRunnerTests
{
    // The .NET Framework starts slower than a .NET 10 process, and the sample's first JIT is on the clock.
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(25);

    [Fact]
    public async Task Discovers_the_legacy_sample_by_attribute_and_speaks_the_current_protocol()
    {
        await using var agent = await StubAgent.StartLegacyAsync(RepoPaths.LegacySampleOutputDir(), Step);

        var ready = await Receive<ReadyMessage>(agent);

        Assert.Equal(RunnerProtocol.Version, ready.ProtocolVersion);
        Assert.Contains(ready.Services, s => s.Name == "Legacy Service" && s.TypeName.EndsWith("LegacyService"));
        Assert.Contains(ready.Services, s => s.Name == "Inherited Legacy Service"); // its lifecycle methods live on a base class
        Assert.Contains(ready.Jobs, j => j.Name == "Legacy Job" && j.DeclaredCron == "0 0 2 * * ?");
        Assert.Contains(ready.Jobs, j => j.Name == "Legacy Batch");
        Assert.Empty(ready.Warnings);

        // Honest zeros, not a claim: net472 has no AssemblyLoadContext, so there is no resolver-based
        // isolation to report — see Application-Developer-Guide §7.
        Assert.Equal(0, ready.Isolation.ResolverCount);
        Assert.Equal(0, ready.Isolation.PrivateResolutionCount);

        await agent.Channel.SendAsync(new ShutdownCommand(5000));
        Assert.True(await DrainUntilExitAsync(agent));
    }

    [Fact]
    public async Task Starting_a_legacy_service_runs_it_and_captures_its_output_and_a_stop_stops_it()
    {
        await using var agent = await StubAgent.StartLegacyAsync(RepoPaths.LegacySampleOutputDir(), Step);
        await Receive<ReadyMessage>(agent);

        await agent.Channel.SendAsync(new StartServiceCommand("Legacy Service"));

        await ReceiveUntil<StateChangedMessage>(agent, m => m.Target == "Legacy Service" && m.State == RunnerState.Running);
        await ReceiveUntil<LogMessage>(agent, m => m.Source == "Legacy Service" && m.Text.Contains("heartbeat") && m.Text.Contains("net472"));

        await agent.Channel.SendAsync(new StopServiceCommand("Legacy Service", TimeoutMs: 5000));
        await ReceiveUntil<StateChangedMessage>(agent, m => m.Target == "Legacy Service" && m.State == RunnerState.Stopped);

        await agent.Channel.SendAsync(new ShutdownCommand(5000));
        Assert.True(await DrainUntilExitAsync(agent));
        Assert.Equal(0, agent.ExitCode);
    }

    [Fact]
    public async Task Running_a_legacy_job_hands_it_the_agents_settings_and_reports_success()
    {
        await using var agent = await StubAgent.StartLegacyAsync(RepoPaths.LegacySampleOutputDir(), Step);
        await Receive<ReadyMessage>(agent);

        var runId = Guid.NewGuid().ToString("N");
        await agent.Channel.SendAsync(new RunJobCommand("Legacy Job", runId, new Dictionary<string, string> { ["source"] = "agent" }));

        // The sample has no settings file, so the one setting it sees is the one sent here.
        await ReceiveUntil<LogMessage>(agent, m => m.Source == "Legacy Job" && m.Text.Contains("running with 1 setting(s) on net472"));

        var result = await ReceiveUntil<JobResultMessage>(agent, m => m.Job == "Legacy Job" && m.RunId == runId);
        Assert.Equal(JobOutcome.Succeeded, result.Outcome);
        Assert.Null(result.Error);

        await agent.Channel.SendAsync(new ShutdownCommand(5000));
        Assert.True(await DrainUntilExitAsync(agent));
    }

    [Fact]
    public async Task Cancelling_an_in_flight_legacy_batch_stops_it_at_a_chunk_boundary_and_reports_Cancelled()
    {
        await using var agent = await StubAgent.StartLegacyAsync(RepoPaths.LegacySampleOutputDir(), Step);
        await Receive<ReadyMessage>(agent);

        // 60 chunks at ~500 ms is ~30 s of work — far longer than this test waits, so an early, clean
        // finish is evidence of the cancel, not of the job ending on its own.
        await agent.Channel.SendAsync(new RunJobCommand("Legacy Batch", "run-1", new Dictionary<string, string> { ["chunks"] = "60" }));
        await ReceiveUntil<LogMessage>(agent, m => m.Text.Contains("chunk 1/60"));

        var sw = Stopwatch.StartNew();
        await agent.Channel.SendAsync(new CancelJobCommand("Legacy Batch"));

        // The plugin's own line: the token reached the job on the .NET Framework and the job acted on it.
        await ReceiveUntil<LogMessage>(agent, m => m.Text.Contains("Legacy batch cancelled after"));

        var result = await ReceiveUntil<JobResultMessage>(agent, m => m.Job == "Legacy Batch");
        sw.Stop();

        Assert.Equal("run-1", result.RunId);
        Assert.Equal(JobOutcome.Cancelled, result.Outcome);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"the run took {sw.Elapsed.TotalSeconds:0.0}s to stop after the cancel - that is the job running to completion, not stopping");

        await agent.Channel.SendAsync(new ShutdownCommand(5000));
        Assert.True(await DrainUntilExitAsync(agent));
    }

    [Fact]
    public async Task Closing_the_pipe_without_a_shutdown_command_still_makes_the_legacy_runner_exit()
    {
        await using var agent = await StubAgent.StartLegacyAsync(RepoPaths.LegacySampleOutputDir(), Step);
        await Receive<ReadyMessage>(agent);

        await agent.Channel.SendAsync(new StartServiceCommand("Legacy Service"));
        await ReceiveUntil<StateChangedMessage>(agent, m => m.Target == "Legacy Service" && m.State == RunnerState.Running);

        // No ShutdownCommand — the agent died, or the pipe dropped. The runner must notice and go.
        await agent.ClosePipeAsync();

        Assert.True(await agent.WaitForExitAsync(Step));
    }

    private static async Task<T> Receive<T>(StubAgent agent) where T : RunnerMessage
    {
        using var cts = new CancellationTokenSource(Step);
        var message = await agent.Channel.ReceiveAsync(cts.Token);
        return Assert.IsType<T>(message);
    }

    private static async Task<T> ReceiveUntil<T>(StubAgent agent, Func<T, bool> predicate) where T : RunnerMessage
    {
        using var cts = new CancellationTokenSource(Step);
        while (true)
        {
            var message = await agent.Channel.ReceiveAsync(cts.Token)
                ?? throw new InvalidOperationException($"Pipe closed while waiting for a {typeof(T).Name} matching the predicate.");

            if (message is T typed && predicate(typed))
            {
                return typed;
            }
        }
    }

    /// <summary>Waits for exit while still reading — a Windows named-pipe flush does not complete until the peer has consumed it, so a test that stops reading can park the runner's outbound pump on its way out. Same lesson as JobCancellationTests.</summary>
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
}
