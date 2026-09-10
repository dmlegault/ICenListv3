using Enlist.Runner.Protocol;

namespace Enlist.Runner.Tests;

/// <summary>
/// The third transport, --listen, where the runner is the server and the agent dials in — the one a
/// container uses, and until now proven only end-to-end through an engine. Driven here the way
/// ContainerRunnerBackend drives it, minus the engine, so a runner-side failure on this transport is
/// attributable to the runner and nothing else.
/// </summary>
public sealed class ListenTransportTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Discovery_completes_over_a_listened_port_exactly_as_over_a_pipe()
    {
        await using var agent = await StubAgent.StartAsync(RepoPaths.SampleServiceOutputDir(), Step, new TcpDialInListener());

        var ready = await Receive<ReadyMessage>(agent);

        Assert.Contains(ready.Services, s => s.Name == "Sample Service" && s.TypeName.EndsWith("SampleService"));
        Assert.Contains(ready.Jobs, j => j.Name == "Sample Job" && j.DeclaredCron == "0 0 2 * * ?");
        Assert.True(ready.Isolation.DepsFilesFound > 0);
    }

    [Fact]
    public async Task A_connection_reset_without_a_shutdown_command_still_makes_the_runner_exit_cleanly()
    {
        await using var agent = await StubAgent.StartAsync(RepoPaths.SampleServiceOutputDir(), Step, new TcpDialInListener(abortiveClose: true));

        await Receive<ReadyMessage>(agent);
        await agent.Channel.SendAsync(new StartServiceCommand("Sample Service"));
        await ReceiveUntil<StateChangedMessage>(agent, m => m.Target == "Sample Service" && m.State == RunnerState.Running);

        // A reset, not a close: what a dead agent looks like from behind a container engine's port proxy.
        // Seen on wslc (2026-09-10) as a runner dying of an unhandled IOException — exit 139 and a stack
        // trace in the container log — instead of stopping its services. It has to mean exactly what
        // end-of-stream means: stop everything, then leave with a clean exit code.
        await agent.ClosePipeAsync();

        Assert.True(await agent.WaitForExitAsync(Step), "the runner did not exit after its connection was reset.");
        Assert.Equal(0, agent.ExitCode);
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
            var message = await agent.Channel.ReceiveAsync(cts.Token);
            Assert.NotNull(message);

            if (message is T typed && predicate(typed))
            {
                return typed;
            }
        }
    }
}
