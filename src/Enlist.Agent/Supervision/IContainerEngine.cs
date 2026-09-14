using Enlist.ControlPlane.Contracts;

namespace Enlist.Agent.Supervision;

/// <summary>
/// The container engine the agent drives — Docker or wslc today, and the seam exists so Podman or a remote
/// engine can be substituted without touching <see cref="ContainerRunnerBackend"/>.
///
/// Deliberately narrow: it covers exactly the container lifecycle the backend needs, and nothing about
/// enList's own concepts. It does not know what an application is, never parses a runner message, and
/// takes no part in discovery — that all belongs above it, unchanged from the process path.
/// </summary>
public interface IContainerEngine
{
    /// <summary>The engine's short name ("docker", "wslc") — reported with the agent's capabilities so the portal can say which engine an agent is on.</summary>
    string Name { get; }

    /// <summary>
    /// Creates and starts one container, returning its id. Returns as soon as the engine reports the
    /// container started — the runner inside it will not be listening yet, which is why
    /// <see cref="ContainerRunnerBackend"/> retries its connect rather than assuming readiness.
    /// </summary>
    Task<string> RunAsync(ContainerSpec spec, CancellationToken ct = default);

    /// <summary>
    /// The host port the engine bound to <paramref name="containerPort"/>. Separate from
    /// <see cref="RunAsync"/> because the port is only knowable after the container starts: the spec
    /// asks for an ephemeral port (host port 0) so concurrent runners cannot collide, and the engine
    /// picks the actual number.
    /// </summary>
    Task<int> GetPublishedPortAsync(string containerId, int containerPort, string protocol = "tcp", CancellationToken ct = default);

    /// <summary>Completes when the container exits, with its exit code. This is the container equivalent of Process.Exited, and is what lets crash-restart behave identically for both backends.</summary>
    Task<int> WaitForExitAsync(string containerId, CancellationToken ct = default);

    /// <summary>Asks the container to stop, escalating to a kill after the grace period. Returns whether it stopped within that period rather than being killed — mirroring ProcessRunnerInstance.StopAsync's return.</summary>
    Task<bool> StopAsync(string containerId, TimeSpan gracePeriod, CancellationToken ct = default);

    /// <summary>Deletes the container. Best-effort by contract: a leaked stopped container costs disk, not correctness.</summary>
    Task RemoveAsync(string containerId, CancellationToken ct = default);

    /// <summary>
    /// Asks the engine whether it is actually usable right now.
    ///
    /// This exists because "configured" and "capable" are different facts, and reporting the first as
    /// the second would make the portal lie. Being started with an image name proves only that someone
    /// passed a flag — not that a container engine is installed, that its daemon is running, or that it
    /// can be reached. An agent in that state accepts a container policy and then fails it.
    ///
    /// Cheap enough to re-run on the heartbeat, which matters: a daemon that was up at startup can be
    /// down ten minutes later, and that is precisely the state worth surfacing.
    /// </summary>
    Task<ContainerEngineProbe> ProbeAsync(CancellationToken ct = default);

    /// <summary>Ids of every container — running or stopped — carrying all of the given labels. Used to find containers a previous life of this agent left behind.</summary>
    Task<IReadOnlyList<string>> ListByLabelsAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct = default);

    /// <summary>
    /// Loads an image archive (`docker save` output) into this engine's image store, AS THE ACCOUNT THE
    /// AGENT RUNS AS. That last part is the point: wslc keeps a separate store per account, so an image an
    /// operator loads in their own session is not one a LocalSystem agent can see. Loading it from inside
    /// the agent puts it in the store the agent will run it from. See <see cref="RunnerImageDropFolder"/>.
    /// </summary>
    Task LoadImageAsync(string archivePath, CancellationToken ct = default);
}

/// <summary>
/// A container could not start because its image is not in the engine's store on this machine. A type of
/// its own, rather than a message to match, so <see cref="ContainerRunnerBackend"/> can react to it - the
/// image drop folder's record of what it loaded is then out of date, and the next start reloads.
/// </summary>
public sealed class ContainerImageMissingException(string message) : InvalidOperationException(message);

/// <summary>The result of asking a container engine whether it is usable. Error is non-null only when Available is false, and carries the engine's own message — an operator needs to know WHY, not just that something is wrong.</summary>
public sealed record ContainerEngineProbe(bool Available, string? Version = null, string? Error = null);

/// <summary>
/// Everything needed to start one runner container. A record rather than a parameter list because C4
/// adds port mappings, networks, environment and resource limits here (docs/03-architecture/Container-Story.md §8),
/// and those must not each become another argument on every call site in between.
/// </summary>
/// <param name="Image">The enList runner image — infrastructure, versioned with enList, never per application (§4.1).</param>
/// <param name="ApplicationPath">The already-extracted package directory on the host, bind-mounted read-only. The same directory the process backend passes as --app.</param>
/// <param name="ControlPort">The port the runner listens on INSIDE the container. Published to host loopback only.</param>
/// <param name="Name">A human-recognizable container name, so `docker ps` during an incident shows which application is which.</param>
/// <param name="Labels">Engine labels stamped on the container. These are how ownership is expressed: a container carries the name of the agent that created it, which is what makes orphan reaping safe on a host shared by several agents.</param>
/// <param name="Ports">The APPLICATION's own ports to publish (docs/03-architecture/Container-Story.md §8.1, level 1). Distinct from ControlPort in both purpose and exposure — see DockerContainerEngine.RunAsync.</param>
/// <param name="Networks">Named container networks to attach to (level 2). enList validates nothing about topology; it attaches and reports.</param>
/// <param name="Env">Non-secret environment variables. Secrets belong in a real store the application reads at startup, never in a policy row the portal renders in plain text.</param>
public sealed record ContainerSpec(
    string Image,
    string ApplicationPath,
    int ControlPort,
    string Name,
    IReadOnlyDictionary<string, string>? Labels = null,
    IReadOnlyList<PortMapping>? Ports = null,
    IReadOnlyList<string>? Networks = null,
    IReadOnlyDictionary<string, string>? Env = null);
