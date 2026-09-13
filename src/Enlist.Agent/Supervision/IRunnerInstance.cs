using Enlist.ControlPlane.Contracts;
using Enlist.Runner.Protocol;

namespace Enlist.Agent.Supervision;

/// <summary>
/// One live runner hosting one application, as <see cref="AgentHost"/> sees it. Created by an
/// <see cref="IRunnerBackend"/>. Two implementations: <see cref="ProcessRunnerInstance"/>
/// (an enlist-runner child process on the far end of a named pipe) and <see cref="ContainerRunnerInstance"/>
/// (a container the agent dials in to).
///
/// The interface is deliberately exactly the surface AgentHost consumes, and nothing more. Nothing
/// was added in anticipation of the second implementation before it existed - RuntimeId and Endpoints
/// were added WITH it (docs/03-architecture/Container-Story.md §6.2), not for it. The same
/// discipline applies from the other direction: WaitForExitAsync exists on ProcessRunnerInstance but
/// is absent here, because AgentHost never calls it - only StopAsync does, internally.
///
/// The invariant that makes this seam worth having: AgentHost must never branch on which
/// implementation it holds. See docs/03-architecture/Container-Story.md §6.4.
/// </summary>
public interface IRunnerInstance : IAsyncDisposable
{
    string ApplicationName { get; }

    /// <summary>
    /// Fires exactly once, whenever the underlying runner exits, for ANY reason. The bool tells the
    /// subscriber whether this was asked for (StopAsync/DisposeAsync ran first) or not — AgentHost
    /// only treats the latter as a crash worth auto-restarting.
    /// </summary>
    event Action<IRunnerInstance, bool>? Exited;

    /// <summary>Completes with the runner's discovery result once it reports in — see AgentHost, which awaits this before auto-starting services or registering jobs.</summary>
    Task<ReadyMessage> WhenReady { get; }

    /// <summary>
    /// The OS process id of the runner, surfaced through to AgentApplicationStatusDto.Pid and asserted
    /// on directly by several agent tests.
    ///
    /// Process-shaped by name and by origin, and a container has no honest answer for it: on Docker
    /// Desktop the container's "host pid" lives inside a Linux VM and means nothing to anything on the
    /// Windows host that might look it up. The container backend therefore reports 0 and puts its real
    /// identity in <see cref="RuntimeId"/> — deliberately, rather than inventing a plausible-looking
    /// number that no one can act on.
    /// </summary>
    int Pid { get; }

    /// <summary>
    /// What this runner is, in terms its own backend can act on: a process id for the process backend,
    /// a container id for the container backend. Added in C2 exactly as docs/03-architecture/Container-Story.md §6.3
    /// proposed — with the second implementation in hand, not before it, so it is shaped by two real
    /// cases rather than one imagined one.
    ///
    /// This is the value to put in a log line or hand to an operator ("docker logs &lt;id&gt;",
    /// "Get-Process -Id &lt;pid&gt;"). Opaque to AgentHost, which never parses it.
    /// </summary>
    string RuntimeId { get; }

    /// <summary>
    /// Ports enList actually published for this unit, empty when it published none.
    ///
    /// Empty is the honest answer for the process backend, not a gap: an in-process service binds
    /// whatever port it likes directly on the host, and enList neither allocates nor knows it. Only a
    /// container has a port mapping enList itself created and can therefore report.
    /// </summary>
    IReadOnlyList<ResolvedEndpointDto> Endpoints => [];

    bool HasExited { get; }

    int? ExitCode { get; }

    Task SendAsync(AgentCommand command, CancellationToken ct = default);

    /// <summary>Asks the runner to shut down, waits up to gracePeriod, then kills it. Returns whether it exited on its own.</summary>
    Task<bool> StopAsync(TimeSpan gracePeriod);
}
