using Enlist.ControlPlane.Contracts;
using Enlist.Runner.Protocol;

namespace Enlist.Agent.Supervision;

/// <summary>
/// Creates runners. The counterpart to <see cref="IRunnerInstance"/>: two things vary independently,
/// so there are two interfaces — how a unit is created (this), and how a live unit behaves (that).
/// See docs/03-architecture/Container-Story.md §6.2.
///
/// AgentHost owns RuntimeFlavor→runner-bin resolution and <see cref="Staging.RunnerStaging"/> rather
/// than a backend, deliberately: a staging or flavor-lookup failure must go straight to Failed and
/// never be retried, whereas a failure inside a backend lands in the crash-backoff path instead.
/// </summary>
public interface IRunnerBackend
{
    /// <summary>
    /// Starts a runner for one application and returns once its control channel is connected. Does NOT
    /// wait for discovery — the caller awaits <see cref="IRunnerInstance.WhenReady"/> for that, on its
    /// own timeout.
    /// </summary>
    Task<IRunnerInstance> StartAsync(RunnerStartRequest request);

    /// <summary>
    /// Releases whatever this backend created per-application outside the instance itself — the staged
    /// runner copy, for the process backend. Called by AgentHost after an application is torn down.
    ///
    /// On the interface rather than left in AgentHost specifically to preserve the invariant in
    /// docs/03-architecture/Container-Story.md §6.4: AgentHost must never branch on backend type. Skipping the staging
    /// copy for containers with an "if (isContainer)" would have been two lines and would have broken
    /// exactly the rule the whole seam exists to hold. A backend with nothing to release does nothing.
    /// </summary>
    void Cleanup(string applicationName);

    /// <summary>
    /// Removes execution units this agent left behind in a PREVIOUS life — called once at startup,
    /// before anything is started, when by definition this agent owns nothing yet.
    ///
    /// Default no-op, which is the correct answer for the process backend: an abnormally dead agent
    /// already takes its child runners with it via the Windows Job Object. The container backend has no
    /// such link and genuinely needs this — a killed agent leaves its containers running indefinitely,
    /// which makes containers WEAKER than processes here, not stronger.
    /// </summary>
    Task ReapOrphansAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Everything a backend needs to start one runner.
///
/// A record rather than a parameter list because each backend legitimately ignores parts of it
/// (transport, staging, ports).
/// Appending a field here is invisible to a backend that does not care about it, whereas appending a
/// positional parameter changes every call site and every implementation.
/// </summary>
/// <param name="ApplicationName">The application this runner will host; carried through for logging and status.</param>
/// <param name="RunnerBinDirectory">The canonical runner build for this application's runtime flavor. What a backend does with it is its own business: the process backend stages a private copy and launches it; the container backend ignores it entirely, because the runner lives in the image.</param>
/// <param name="ApplicationPath">The extracted package directory the runner discovers plugins in.</param>
/// <param name="Isolation">The placement the policy declared. Carries the ports and networks only a container backend can act on; the process backend ignores it.</param>
/// <param name="OnMessage">Invoked for every message the runner sends, in receive order.</param>
public sealed record RunnerStartRequest(
    string ApplicationName,
    string RunnerBinDirectory,
    string ApplicationPath,
    IsolationSpec Isolation,
    Func<IRunnerInstance, RunnerMessage, Task> OnMessage);
