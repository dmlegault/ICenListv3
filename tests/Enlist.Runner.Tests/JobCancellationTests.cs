using System.Diagnostics;

using Enlist.Runner.Protocol;

namespace Enlist.Runner.Tests;

/// <summary>
/// Stopping ONE IN-FLIGHT RUN against a real runner process.
///
/// This is a different thing from the scheduler-level stop the agent offers, which unregisters a job
/// so it stops FIRING again and never reaches a runner at all. Only the plugin can stop work already
/// underway, because only it knows where stopping is safe; the runner does nothing but cancel the
/// token the job was handed.
///
/// Driven against OrderProcessor's Ledger Rebuild — a chunked batch that checks its token at each
/// chunk boundary and logs every chunk, so "did it actually stop" is observable from the log stream
/// rather than inferred from a timeout.
/// </summary>
public sealed class JobCancellationTests : IAsyncLifetime
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(20);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Cancelling_an_in_flight_run_stops_it_and_reports_it_as_cancelled_not_failed()
    {
        await using var agent = await StubAgent.StartAsync(RepoPaths.OrderProcessorOutputDir(), Step);
        await Receive<ReadyMessage>(agent);

        // 60 chunks at ~500ms is ~30s of work — far longer than this test is willing to wait, which is
        // what makes an early, clean finish evidence of the cancel rather than of the job ending.
        await agent.Channel.SendAsync(new RunJobCommand(
            "Ledger Rebuild", "run-1", new Dictionary<string, string> { ["chunks"] = "60" }));

        // Let it get properly underway first — cancelling before the run registers would be testing the
        // "no run in flight" path by accident.
        await ReceiveLogContaining(agent, "chunk 1/60");

        var sw = Stopwatch.StartNew();
        await agent.Channel.SendAsync(new CancelJobCommand("Ledger Rebuild"));

        // The plugin's own line, not the runner's: proof the token reached the job and the job acted on
        // it, rather than the runner merely claiming to have asked.
        await ReceiveLogContaining(agent, "Ledger rebuild cancelled after");

        var result = await Receive<JobResultMessage>(agent);
        sw.Stop();

        Assert.Equal("run-1", result.RunId);
        Assert.Equal(JobOutcome.Cancelled, result.Outcome);
        Assert.Contains("cancelled", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"the run took {sw.Elapsed.TotalSeconds:0.0}s to stop after the cancel - that is the job running to completion, not stopping");

        await agent.Channel.SendAsync(new ShutdownCommand(5000));
        Assert.True(await DrainUntilExitAsync(agent));
    }

    [Fact]
    public async Task Cancelling_a_job_with_nothing_in_flight_reports_it_rather_than_failing_silently()
    {
        await using var agent = await StubAgent.StartAsync(RepoPaths.OrderProcessorOutputDir(), Step);
        await Receive<ReadyMessage>(agent);

        // Nothing has been run. This is the common way to get here — a run that already finished — so
        // it is an informational line, not a fault.
        await agent.Channel.SendAsync(new CancelJobCommand("Ledger Rebuild"));

        var log = await ReceiveLogContaining(agent, "no run of this job is in flight");
        Assert.Equal(LogLevel.Information, log.Level);

        await agent.Channel.SendAsync(new ShutdownCommand(5000));
        Assert.True(await DrainUntilExitAsync(agent));
    }

    [Fact]
    public async Task Cancelling_a_job_that_does_not_exist_faults_rather_than_going_quiet()
    {
        await using var agent = await StubAgent.StartAsync(RepoPaths.OrderProcessorOutputDir(), Step);
        await Receive<ReadyMessage>(agent);

        await agent.Channel.SendAsync(new CancelJobCommand("No Such Job"));

        var fault = await Receive<FaultedMessage>(agent);
        Assert.Contains("No Such Job", fault.Error);

        await agent.Channel.SendAsync(new ShutdownCommand(5000));
        Assert.True(await DrainUntilExitAsync(agent));
    }

    /// <summary>
    /// Shutdown used to ignore jobs entirely: StopAllAsync walked the running SERVICES and the process
    /// then exited out from under any run still executing. This asserts the run is cancelled and drains
    /// before the process leaves.
    /// </summary>
    [Fact]
    public async Task Shutdown_cancels_an_in_flight_run_instead_of_exiting_out_from_under_it()
    {
        await using var agent = await StubAgent.StartAsync(RepoPaths.OrderProcessorOutputDir(), Step);
        await Receive<ReadyMessage>(agent);

        await agent.Channel.SendAsync(new RunJobCommand(
            "Ledger Rebuild", "run-1", new Dictionary<string, string> { ["chunks"] = "60" }));
        await ReceiveLogContaining(agent, "chunk 1/60");

        var sw = Stopwatch.StartNew();
        await agent.Channel.SendAsync(new ShutdownCommand(15000));

        // The job's own line again — the runner waited for the run to unwind rather than racing it.
        await ReceiveLogContaining(agent, "Ledger rebuild cancelled after");

        Assert.True(await DrainUntilExitAsync(agent));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15),
            $"shutdown took {sw.Elapsed.TotalSeconds:0.0}s - the grace period expired rather than the run stopping");
    }

    /// <summary>
    /// Regression guard for a defect this feature exposed: a synchronous plugin method used to run to
    /// completion ON THE READ-LOOP THREAD, because `_ = DispatchAsync(command)` executes inline until
    /// the first await that actually yields — and a void method never yields. Every subsequent command
    /// sat unread until the job finished, which made Cancel, Stop and Shutdown useless for exactly the
    /// long-running work that needs them.
    /// </summary>
    [Fact]
    public async Task A_long_synchronous_job_does_not_block_the_runner_from_reading_further_commands()
    {
        await using var agent = await StubAgent.StartAsync(RepoPaths.OrderProcessorOutputDir(), Step);
        await Receive<ReadyMessage>(agent);

        await agent.Channel.SendAsync(new RunJobCommand(
            "Ledger Rebuild", "run-1", new Dictionary<string, string> { ["chunks"] = "60" }));
        await ReceiveLogContaining(agent, "chunk 1/60");

        // A command for an entirely DIFFERENT target, so this measures the read loop rather than any
        // per-job serialization: it must be answered while the job is still running.
        var sw = Stopwatch.StartNew();
        await agent.Channel.SendAsync(new StartServiceCommand("No Such Service"));
        var fault = await Receive<FaultedMessage>(agent);
        sw.Stop();

        Assert.Contains("No Such Service", fault.Error);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"the runner took {sw.Elapsed.TotalSeconds:0.0}s to answer while a synchronous job ran - the read loop is blocked");

        await agent.Channel.SendAsync(new ShutdownCommand(5000));
        Assert.True(await DrainUntilExitAsync(agent));
    }

    /// <summary>
    /// Waits for the runner to exit while STILL READING the channel, which a real agent does and a test
    /// that simply stops reading does not. A Windows named-pipe flush does not complete until the peer
    /// has consumed what is buffered, so whatever the runner emits on its way out — the cancelled
    /// JobResult, a final log line — parks its outbound pump and the process never reaches its exit.
    /// The deadlock is the test's, not the runner's, but it looks exactly like a hung runner.
    /// </summary>
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

    private static async Task<LogMessage> ReceiveLogContaining(StubAgent agent, string fragment)
    {
        using var cts = new CancellationTokenSource(Step);
        while (true)
        {
            var message = await agent.Channel.ReceiveAsync(cts.Token)
                ?? throw new InvalidOperationException($"channel closed while waiting for a log containing '{fragment}'");
            if (message is LogMessage log && log.Text.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return log;
            }
        }
    }
}
