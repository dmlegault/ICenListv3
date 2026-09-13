using Enlist.ControlPlane.Contracts;
using Enlist.Agent.Logging;
using Enlist.Agent.Supervision;
using Enlist.Runner.Protocol;
using Enlist.TestSupport;

namespace Enlist.Agent.Tests;

/// <summary>
/// docs/03-architecture/Container-Story.md §12 — exercises the AGENT's own socket path, not a test
/// reimplementation of it.
///
/// Enlist.Runner.Tests' SocketTransportTests already proves the runner speaks the protocol over a Unix
/// domain socket, but it does so through StubAgent, which builds its own listener. That leaves the
/// code that will actually run in production — ProcessRunnerBackend choosing and driving an
/// IChannelListener — unproven. These tests use the real ProcessRunnerBackend, the real staged runner
/// binary, and the real IRunnerInstance the agent holds.
///
/// AF_UNIX is supported on Windows 10 1803+, so this runs on the project's Windows dev box.
/// </summary>
public sealed class ProcessRunnerBackendTransportTests : IAsyncLifetime
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "enlist-transport-tests-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch
        {
        }

        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(RunnerTransport.NamedPipe)]
    [InlineData(RunnerTransport.UnixSocket)]
    public async Task The_backend_starts_a_runner_and_completes_discovery_over_either_transport(RunnerTransport transport)
    {
        var instance = await StartAsync(transport);
        await using (instance)
        {
            var ready = await instance.WhenReady.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Contains(ready.Services, s => s.Name == "Sample Service");
            Assert.True(instance.Pid > 0);
            Assert.False(instance.HasExited);

            // The transport must not change what the agent sees — same discovery, same protocol
            // version. If a socket somehow delivered a subtly different Ready, this is where it shows.
            Assert.Equal(RunnerProtocol.Version, ready.ProtocolVersion);
        }
    }

    [Theory]
    [InlineData(RunnerTransport.NamedPipe)]
    [InlineData(RunnerTransport.UnixSocket)]
    public async Task A_runner_stops_gracefully_over_either_transport(RunnerTransport transport)
    {
        var instance = await StartAsync(transport);
        await using (instance)
        {
            await instance.WhenReady.WaitAsync(TimeSpan.FromSeconds(20));

            // True means it exited on its own after ShutdownCommand rather than being killed — i.e. the
            // command actually reached the runner over this transport and it acted on it. A transport
            // that only worked one way would still return false here.
            Assert.True(
                await instance.StopAsync(TimeSpan.FromSeconds(15)),
                $"the runner did not shut down gracefully over {transport} - it had to be killed.");

            Assert.True(instance.HasExited);
        }
    }

    private async Task<IRunnerInstance> StartAsync(RunnerTransport transport)
    {
        var stagingRoot = Path.Combine(_dataRoot, "Runners");
        var logRoot = Path.Combine(_dataRoot, "Logs");
        Directory.CreateDirectory(logRoot);

        var appName = "TransportTest" + Guid.NewGuid().ToString("N")[..8];

        // jobObject: null - this test is about the transport, and a Job Object is orthogonal to it.
        var backend = new ProcessRunnerBackend(
            jobObject: null,
            connectTimeout: TimeSpan.FromSeconds(20),
            logSink: new AgentFileLogSink(logRoot, null),
            stagingRoot: stagingRoot,
            transport: transport);

        // The backend stages its own runner copy — it takes the canonical runner-bin directory
        // now, not an already-staged exe.
        return await backend.StartAsync(new RunnerStartRequest(
            appName, RepoPaths.RunnerBinDirectory(), RepoPaths.SampleServiceDir(), IsolationSpec.ProcessDefault, (_, _) => Task.CompletedTask));
    }
}
