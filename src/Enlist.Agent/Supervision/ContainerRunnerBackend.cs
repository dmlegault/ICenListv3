using Enlist.ControlPlane.Contracts;
using System.Net;
using System.Net.Sockets;

using Enlist.Agent.Logging;
using Enlist.Runner.Protocol;

namespace Enlist.Agent.Supervision;

/// <summary>
/// The second <see cref="IRunnerBackend"/>: starts a runner inside a container instead of as a child
/// process. Design A from docs/03-architecture/Container-Story.md — one generic runner image, the application arriving
/// as a read-only bind mount of the package directory the agent already extracted.
///
/// The whole reason this can exist as an additive change is C0: AgentHost holds IRunnerInstance and
/// never branches on which backend produced it, so reconciliation, crash-backoff, job scheduling,
/// command dispatch, status assembly and log handling all work here untouched.
/// </summary>
public sealed class ContainerRunnerBackend : IRunnerBackend
{
    /// <summary>
    /// The port the runner listens on INSIDE the container. Fixed, and safe to fix: each container has
    /// its own network namespace, so every runner can use the same internal port without colliding.
    /// The HOST side is what varies, and the engine allocates that ephemerally.
    /// </summary>
    private const int ControlPortInContainer = 5000;

    /// <summary>Label keys stamped on every container this backend creates. Namespaced so they cannot collide with an operator's own labels on the same host.</summary>
    private const string AgentLabel = "enlist.agent";
    private const string ApplicationLabel = "enlist.application";

    private readonly IContainerEngine _engine;
    private readonly string _image;
    private readonly TimeSpan _connectTimeout;
    private readonly AgentFileLogSink _logSink;
    private readonly string _agentName;

    /// <param name="agentName">Stamped on every container as an ownership label. It must be STABLE across restarts — that is the whole basis of orphan reaping — which is why it is the agent's registered name and not something generated at boot.</param>
    /// <param name="images">The agent's Images folder, loaded into the engine before each container start - see <see cref="RunnerImageDropFolder"/>. Null means none: images are put in the engine some other way.</param>
    public ContainerRunnerBackend(IContainerEngine engine, string image, TimeSpan connectTimeout, AgentFileLogSink logSink, string agentName, string? hostAddress = null, RunnerImageDropFolder? images = null)
    {
        _engine = engine;
        _image = image;
        _connectTimeout = connectTimeout;
        _logSink = logSink;
        _agentName = agentName;
        _images = images;

        // Reported alongside every resolved port so an operator gets somewhere they can actually reach.
        // The machine name rather than an IP: a host commonly has several addresses (and on Docker
        // Desktop some of them are the VM's), and picking one arbitrarily would be a coin flip.
        _hostAddress = hostAddress ?? Environment.MachineName;
    }

    private readonly string _hostAddress;

    private readonly RunnerImageDropFolder? _images;

    /// <param name="runnerBinDirectory">Ignored — the runner lives in the image, so there is nothing to stage. This is why staging moved behind the seam in C3: leaving it in AgentHost meant every containerized application still paid for a runner copy nobody reads, and skipping it there would have required the one thing the seam forbids, an "if (isContainer)" in AgentHost.</param>
    public async Task<IRunnerInstance> StartAsync(RunnerStartRequest request)
    {
        var (applicationName, _, applicationPath, isolation, onMessage) = request;

        // Name is cosmetic but load-bearing during an incident: `docker ps` should say which
        // application a container belongs to without anyone having to correlate ids. Suffixed because
        // a name must be unique and a restart can briefly overlap the outgoing container.
        var spec = new ContainerSpec(
            // A per-policy image override beats the agent's default. Its main use is pinning one
            // application to a specific enList release during a migration, which is precisely the kind
            // of thing that must not require reconfiguring the whole agent. Silently ignoring this
            // field — reading it and doing nothing would make the API accept a
            // setting it never honoured.
            isolation.Image ?? _image,
            applicationPath,
            ControlPortInContainer,
            $"enlist-{Sanitize(applicationName)}-{Guid.NewGuid().ToString("N")[..8]}",
            new Dictionary<string, string>
            {
                [AgentLabel] = _agentName,
                [ApplicationLabel] = applicationName,
            },
            isolation.Ports,
            isolation.Networks,
            isolation.Env);

        // Whatever is waiting in the Images folder goes into the engine first, as this agent's account -
        // the only store this agent's containers run from. Before EVERY start, not once at boot: that
        // is what lets an image copied in after the install reach the retry of an application that
        // failed for want of it. Costs a directory listing when there is nothing new.
        if (_images is not null)
        {
            await _images.LoadNewImagesAsync().ConfigureAwait(false);
        }

        string containerId;
        try
        {
            containerId = await _engine.RunAsync(spec).ConfigureAwait(false);
        }
        catch (ContainerImageMissingException) when (_images is not null)
        {
            // The folder's record said the image was loaded; the engine says it is not there - removed
            // by hand, or an engine reset. The engine is right, so the record goes, and the retry this
            // failure schedules loads the archive again.
            await _images.ForgetAsync().ConfigureAwait(false);
            throw;
        }

        Stream transport;
        int hostPort;
        try
        {
            hostPort = await _engine.GetPublishedPortAsync(containerId, ControlPortInContainer).ConfigureAwait(false);
            transport = await ConnectAsync(hostPort).ConfigureAwait(false);
        }
        catch
        {
            // Nothing was handed to an instance, so nothing else will clean this up.
            await _engine.RemoveAsync(containerId).ConfigureAwait(false);
            throw;
        }

        await _logSink.WriteAgentLogAsync(
            $"{applicationName}: started in container {containerId[..Math.Min(12, containerId.Length)]} " +
            $"(image {_image}, control channel on 127.0.0.1:{hostPort}).").ConfigureAwait(false);

        // Resolved AFTER the container is up, because a dynamic mapping has no answer before then — the
        // engine picks the host port. This is why the status snapshot reports resolved endpoints
        // separately from the declared PortMapping: only one of the two is knowable in advance.
        var endpoints = await ResolveEndpointsAsync(containerId, isolation.Ports, applicationName).ConfigureAwait(false);

        var channel = new MessageChannel<AgentCommand, RunnerMessage>(transport);
        channel.MessageSkipped += line => _ = _logSink.WriteAgentLogAsync($"{applicationName}: {line}");
        var instance = new ContainerRunnerInstance(applicationName, _engine, containerId, transport, channel, onMessage, _logSink.WriteAgentLogAsync, endpoints);
        instance.StartPumps();
        return instance;
    }

    /// <summary>
    /// Dials the container's published control port, retrying until the runner inside is actually
    /// listening. The retry is required, not defensive: the engine reports the container started as
    /// soon as its process is created, which is well before the .NET runtime has come up and bound the
    /// port, so the first several connects legitimately get refused.
    ///
    /// Refused is the kind outcome. An engine's port proxy can instead ACCEPT the connection itself the
    /// moment the container exists, dial inside, and close what it accepted when nothing is listening
    /// there yet — so a connect "succeeds" against nothing, and the channel then reads end-of-stream
    /// before a single byte, indistinguishable from a runner that died during discovery. wslc 2.9.11.0
    /// does exactly this (verified with a container that never listens: every connect accepted, every
    /// one closed), and Docker's userland proxy is built the same way. So an accepted connection is
    /// not handed over until the far end has sent something or stayed open for a moment; one that
    /// closes first was the proxy, and is dialled again.
    /// </summary>
    private async Task<Stream> ConnectAsync(int hostPort)
    {
        using var cts = new CancellationTokenSource(_connectTimeout);

        while (true)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, hostPort, cts.Token).ConfigureAwait(false);
                if (await StaysOpenAsync(client.Client).ConfigureAwait(false))
                {
                    return client.GetStream();
                }
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                // Refused, or the deadline passed mid-dial — both are answered below.
            }

            client.Dispose();

            if (cts.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"The runner container never accepted a control connection on 127.0.0.1:{hostPort} within {_connectTimeout.TotalSeconds:0}s.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// How long an accepted, silent connection has to stay open before it is believed. A proxy with
    /// nothing to forward to closes within milliseconds; a real runner never closes before it has
    /// spoken. This is the most the check delays a container start, and only while the runner is
    /// still discovering.
    /// </summary>
    private static readonly TimeSpan ProxySettle = TimeSpan.FromSeconds(1);

    /// <summary>True once the far end has sent something or stayed connected through the settle window; false if it closed first.</summary>
    private static async Task<bool> StaysOpenAsync(Socket socket)
    {
        var deadline = Environment.TickCount64 + (long)ProxySettle.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            // Readable with nothing to read is how a socket reports the far end having closed.
            if (socket.Poll(0, SelectMode.SelectRead))
            {
                try
                {
                    return socket.Available > 0;
                }
                catch (SocketException)
                {
                    return false;
                }
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Nothing to release: this backend stages no files, and the container itself is removed by ContainerRunnerInstance.DisposeAsync.</summary>
    public void Cleanup(string applicationName)
    {
    }

    /// <summary>
    /// Removes every container still labelled as belonging to THIS agent. Safe to do unconditionally at
    /// startup: nothing has been started yet, so any such container is by definition a leftover from a
    /// previous life of this agent.
    ///
    /// Scoped by the agent-name label rather than by the "enlist-" name prefix, and that distinction is
    /// load-bearing: several agents commonly share one container engine (two do on this project's own
    /// dev box), and a prefix-based sweep by one agent would destroy another agent's LIVE containers.
    /// </summary>
    public async Task ReapOrphansAsync(CancellationToken ct = default)
    {
        IReadOnlyList<string> orphans;
        try
        {
            orphans = await _engine.ListByLabelsAsync(new Dictionary<string, string> { [AgentLabel] = _agentName }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // An unreachable engine is already reported through the capability probe; failing startup
            // over a cleanup pass would be a far worse outcome than a few leftover containers.
            await _logSink.WriteAgentLogAsync($"Could not check for orphaned containers: {ex.Message}").ConfigureAwait(false);
            return;
        }

        if (orphans.Count == 0)
        {
            return;
        }

        foreach (var id in orphans)
        {
            await _engine.RemoveAsync(id, ct).ConfigureAwait(false);
        }

        // Logged rather than silent: containers surviving a restart means this agent did not exit
        // cleanly last time, which is worth knowing even though it has just been tidied up.
        await _logSink.WriteAgentLogAsync(
            $"Removed {orphans.Count} orphaned container(s) left by a previous run of this agent.").ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the engine what it actually bound for each declared port. A mapping that cannot be read back
    /// is skipped with a warning rather than failing the start: the application is running and healthy,
    /// and losing one line of endpoint reporting is not a reason to tear it down.
    /// </summary>
    private async Task<IReadOnlyList<ResolvedEndpointDto>> ResolveEndpointsAsync(string containerId, IReadOnlyList<PortMapping>? ports, string applicationName)
    {
        if (ports is null || ports.Count == 0)
        {
            return [];
        }

        var resolved = new List<ResolvedEndpointDto>();
        foreach (var port in ports)
        {
            try
            {
                var hostPort = await _engine.GetPublishedPortAsync(containerId, port.ContainerPort, port.Protocol).ConfigureAwait(false);
                resolved.Add(new ResolvedEndpointDto(port.Name, port.Protocol, port.ContainerPort, hostPort, _hostAddress));
            }
            catch (Exception ex)
            {
                await _logSink.WriteAgentLogAsync(
                    $"{applicationName}: could not read the published host port for {port.ContainerPort}/{port.Protocol}: {ex.Message}").ConfigureAwait(false);
            }
        }

        return resolved;
    }

    /// <summary>Docker names allow only [a-zA-Z0-9][a-zA-Z0-9_.-]*, while an enList application name is free text.</summary>
    private static string Sanitize(string name)
    {
        var cleaned = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '-').ToArray());
        return string.IsNullOrEmpty(cleaned) ? "app" : cleaned;
    }
}
