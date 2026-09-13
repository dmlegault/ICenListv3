using Enlist.Runner.Protocol;

namespace Enlist.Runner.Tests;

/// <summary>
/// docs/03-architecture/Container-Story.md §12 — the control channel is transport-pluggable. These drive the
/// SAME real enlist-runner.exe process and the SAME real plugin as RunnerLifecycleTests, but over a
/// Unix domain socket instead of a named pipe.
///
/// The point is that a socket is not merely wired up but genuinely equivalent: discovery, a service
/// start with console capture, and disconnect-detection all behave identically. Without that, the
/// container work would have had two unproven variables at once (the container AND the socket), and
/// a failure could not be attributed to either.
///
/// AF_UNIX is supported on Windows 10 1803+, which is why this runs on the project's Windows dev box
/// rather than waiting for a Linux container to exist.
/// </summary>
public sealed class SocketTransportTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Discovery_completes_over_a_unix_domain_socket_exactly_as_over_a_pipe()
    {
        await using var agent = await StubAgent.StartAsync(
            RepoPaths.SampleServiceOutputDir(), Step, new UnixSocketChannelListener());

        var ready = await Receive<ReadyMessage>(agent);

        // Deliberately the same assertions RunnerLifecycleTests makes over a pipe — "same lifecycle,
        // different transport" is the claim, so the assertions have to be the same too.
        Assert.Contains(ready.Services, s => s.Name == "Sample Service" && s.TypeName.EndsWith("SampleService"));
        Assert.Contains(ready.Jobs, j => j.Name == "Sample Job" && j.DeclaredCron == "0 0 2 * * ?");
        Assert.Empty(ready.Warnings);
        Assert.True(ready.Isolation.DepsFilesFound > 0);
    }

    [Fact]
    public async Task Commands_and_captured_console_output_flow_both_ways_over_a_socket()
    {
        await using var agent = await StubAgent.StartAsync(
            RepoPaths.SampleServiceOutputDir(), Step, new UnixSocketChannelListener());

        await Receive<ReadyMessage>(agent);
        await agent.Channel.SendAsync(new StartServiceCommand("Sample Service"));

        // Proves the full round trip, not just that bytes arrived: a command travelled agent→runner,
        // the plugin actually ran, and its Console.WriteLine came back runner→agent as a LogMessage.
        var running = await ReceiveUntil<StateChangedMessage>(agent, m => m.Target == "Sample Service" && m.State == RunnerState.Running);
        Assert.Equal(RunnerTargetKind.Service, running.TargetKind);

        var log = await ReceiveUntil<LogMessage>(agent, m => m.Source == "Sample Service");
        Assert.False(string.IsNullOrWhiteSpace(log.Text));
    }

    [Fact]
    public async Task Closing_the_socket_without_a_shutdown_command_still_makes_the_runner_exit()
    {
        await using var agent = await StubAgent.StartAsync(
            RepoPaths.SampleServiceOutputDir(), Step, new UnixSocketChannelListener());

        await Receive<ReadyMessage>(agent);
        await agent.ClosePipeAsync();

        // The "agent vanished" safety net has to hold on every transport, or a runner orphaned by a
        // dead agent would sit there forever. A socket signals EOF on close the same way a pipe does.
        Assert.True(await agent.WaitForExitAsync(Step), "the runner did not exit after its socket was closed.");
    }

    [Fact]
    public async Task The_runner_reports_the_current_protocol_version()
    {
        await using var agent = await StubAgent.StartAsync(RepoPaths.SampleServiceOutputDir(), Step);

        var ready = await Receive<ReadyMessage>(agent);

        // The value the agent compares against (see AgentHost.StartApplicationAsync). If the runner
        // ever stops sending it, this catches it here rather than as every application failing to start.
        Assert.Equal(RunnerProtocol.Version, ready.ProtocolVersion);
        Assert.Null(RunnerProtocol.DescribeMismatch(ready.ProtocolVersion));
    }

    private static Task<T> Receive<T>(StubAgent agent) where T : RunnerMessage => agent.NextAsync<T>(Step);

    private static Task<T> ReceiveUntil<T>(StubAgent agent, Func<T, bool> predicate) where T : RunnerMessage => agent.NextMatchingAsync(Step, predicate);
}
