using System.Net;
using System.Net.Sockets;

using Enlist.Agent.Logging;
using Enlist.Agent.Supervision;
using Enlist.ControlPlane.Contracts;
using Enlist.Runner.Protocol;
using Enlist.TestSupport;

namespace Enlist.Agent.Tests;

/// <summary>
/// The backend's dial-in against an engine port proxy that accepts before anything inside the container
/// is listening. wslc's does (verified 2026-09-10: a container that never listens still accepts every
/// connect on its published port, then closes it), and under full-suite load it cost one run a
/// "closed its control channel before completing discovery" that read as a runner crash.
///
/// The only container tests with no engine, and deliberately so: what is under test is the proxy's
/// behaviour, which a real engine only shows under load. A stand-in engine hands the backend a port
/// this class listens on itself, and the far end plays the proxy — accept, close — for as many connects
/// as the case needs before playing the runner.
/// </summary>
public sealed class ContainerControlChannelTests : IAsyncLifetime
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "enlist-proxy-tests-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        return Task.CompletedTask;
    }

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

    [Fact]
    public async Task A_connection_the_proxy_closes_before_the_runner_speaks_is_dialled_again()
    {
        await using var far = new FarEnd(closeFirst: 2);
        var backend = new ContainerRunnerBackend(new PortOnlyEngine(far.Port), "stand-in", TimeSpan.FromSeconds(30), Sink(), "test-proxy");

        await using var instance = await backend.StartAsync(Request());
        var ready = await instance.WhenReady.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains(ready.Services, s => s.Name == "Stand-in Service");
        Assert.Equal(3, far.Accepted);

        // Closing the far end first lets the instance drain its receive loop instead of waiting it out.
        await far.DisposeAsync();
    }

    [Fact]
    public async Task A_proxy_that_never_finds_a_listener_fails_the_start_and_removes_the_container()
    {
        await using var far = new FarEnd(closeFirst: int.MaxValue);
        var engine = new PortOnlyEngine(far.Port);
        var backend = new ContainerRunnerBackend(engine, "stand-in", TimeSpan.FromSeconds(3), Sink(), "test-proxy");

        await Assert.ThrowsAsync<TimeoutException>(() => backend.StartAsync(Request()));

        Assert.True(far.Accepted > 1, "the backend gave up after a single accepted-then-closed connection.");
        Assert.True(engine.Removed, "the container that never answered was left behind.");
    }

    private AgentFileLogSink Sink() => new(_dataRoot, null);

    private static RunnerStartRequest Request() =>
        new(RepoPaths.UniqueAppName(), "", RepoPaths.SampleServiceDir(), IsolationSpec.ProcessDefault, (_, _) => Task.CompletedTask);

    /// <summary>An engine that "starts" a container by handing back the port this test listens on. Everything else is the least that keeps the backend and its instance whole.</summary>
    private sealed class PortOnlyEngine(int port) : IContainerEngine
    {
        public bool Removed { get; private set; }

        public string Name => "stand-in";

        public Task<string> RunAsync(ContainerSpec spec, CancellationToken ct = default) => Task.FromResult("standin00001");

        public Task<int> GetPublishedPortAsync(string containerId, int containerPort, string protocol = "tcp", CancellationToken ct = default) => Task.FromResult(port);

        public async Task<int> WaitForExitAsync(string containerId, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public Task<bool> StopAsync(string containerId, TimeSpan gracePeriod, CancellationToken ct = default) => Task.FromResult(true);

        public Task RemoveAsync(string containerId, CancellationToken ct = default)
        {
            Removed = true;
            return Task.CompletedTask;
        }

        public Task<ContainerEngineProbe> ProbeAsync(CancellationToken ct = default) => Task.FromResult(new ContainerEngineProbe(true));

        public Task<IReadOnlyList<string>> ListByLabelsAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    /// <summary>The published port's far end: the proxy for the first <c>closeFirst</c> connections, the runner for any after.</summary>
    private sealed class FarEnd : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly List<MessageChannel<RunnerMessage, AgentCommand>> _held = [];
        private readonly Task _serve;
        private int _accepted;
        private bool _disposed;

        public FarEnd(int closeFirst)
        {
            _listener.Start();
            _serve = ServeAsync(closeFirst);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Accepted => Volatile.Read(ref _accepted);

        private async Task ServeAsync(int closeFirst)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var socket = await _listener.AcceptSocketAsync(_stop.Token);
                    if (Interlocked.Increment(ref _accepted) <= closeFirst)
                    {
                        // The proxy: accepted, found nothing inside to forward to, closed.
                        socket.Close();
                        continue;
                    }

                    // The runner: speaks first, and stays.
                    var channel = new MessageChannel<RunnerMessage, AgentCommand>(new NetworkStream(socket, ownsSocket: true));
                    lock (_held)
                    {
                        _held.Add(channel);
                    }

                    await channel.SendAsync(
                        new ReadyMessage([new ServiceInfo("Stand-in Service", null, "StandIn")], [], [], new IsolationInfo(1, 0, 0, []), null, RunnerProtocol.Version),
                        _stop.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stop.Cancel();
            _listener.Stop();

            List<MessageChannel<RunnerMessage, AgentCommand>> held;
            lock (_held)
            {
                held = [.. _held];
            }

            foreach (var channel in held)
            {
                await channel.DisposeAsync();
            }

            try
            {
                await _serve;
            }
            catch
            {
            }

            _stop.Dispose();
        }
    }
}
