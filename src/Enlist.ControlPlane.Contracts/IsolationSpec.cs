namespace Enlist.ControlPlane.Contracts;

/// <summary>
/// HOW an application is hosted on a qualifying agent — in the agent's own process tree, or inside a
/// container. See docs/03-architecture/Container-Story.md §4.2.
///
/// Deliberately NOT a RuntimeFlavor. Flavor (net10.0 / net472) is a fact about the PACKAGE — what it
/// needs in order to run at all — and a containerized net10.0 package is still net10.0. Isolation is a
/// fact about the PLACEMENT. Keeping the two axes independent is what makes the property in §2.1 true:
/// the same package digest can run in-process on one agent and containerized on another, at the same
/// time, with no rebuild — so containerization is a deployment decision you can A/B and roll back,
/// rather than a build-time fork.
///
/// Stored as a single JSON column (ApplicationPolicyEntity.IsolationJson) rather than spread across
/// columns, following the same reasoning already applied to TagSelectorJson and CronOverridesJson: it
/// is always read and written as a whole and never queried by individual key — which is why ports,
/// networks and environment could be added to it without a migration or an entity change.
/// </summary>
/// <param name="Mode">See <see cref="IsolationModes"/>. Defaults to process.</param>
/// <param name="Image">Container mode only — null means the agent's configured default runner image. An override exists mainly for pinning a specific enList release during a migration.</param>
/// <param name="Ports">Container ports to publish. A null HostPort lets the engine allocate one, which is what makes a single rule work across many agents.</param>
/// <param name="Networks">Named container networks to attach to.</param>
/// <param name="Env">Non-secret environment only — secrets belong in a real store the application reads at startup, never in a policy row the portal renders in plain text (docs/03-architecture/Container-Story.md §15).</param>
public sealed record IsolationSpec(
    string Mode = IsolationModes.Process,
    string? Image = null,
    IReadOnlyList<PortMapping>? Ports = null,
    IReadOnlyList<string>? Networks = null,
    IReadOnlyDictionary<string, string>? Env = null)
{
    /// <summary>What a null IsolationJson column means. The two are interchangeable.</summary>
    public static readonly IsolationSpec ProcessDefault = new();

    public bool IsContainer => string.Equals(Mode, IsolationModes.Container, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether two rules matching the same agent describe the same hosting, for conflict detection.
    ///
    /// Do NOT use the compiler-generated record equality for this. A positional record compares its
    /// members with EqualityComparer&lt;T&gt;.Default, which for <see cref="Ports"/>, <see cref="Networks"/>
    /// and <see cref="Env"/> means REFERENCE equality — two specs with identical port lists in
    /// different list instances would compare unequal, and every policy carrying ports would report a
    /// phantom conflict on every reconcile pass — an application restarted forever, for a difference
    /// nobody can see.
    /// </summary>
    public bool AgreesWith(IsolationSpec other) =>
        string.Equals(Mode, other.Mode, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Image, other.Image, StringComparison.OrdinalIgnoreCase) &&
        (Ports ?? []).SequenceEqual(other.Ports ?? []) &&
        (Networks ?? []).SequenceEqual(other.Networks ?? [], StringComparer.OrdinalIgnoreCase) &&
        (Env ?? new Dictionary<string, string>()).Count == (other.Env ?? new Dictionary<string, string>()).Count &&
        (Env ?? new Dictionary<string, string>()).All(kv =>
            (other.Env ?? new Dictionary<string, string>()).TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <summary>
    /// The same hosting with only the mode restated — what an edit that re-selects "process or
    /// container" and touches nothing else must produce. Image, Ports, Networks and Env describe a
    /// container: they travel with the spec while the mode stays container and are dropped when it
    /// becomes process. Nothing reads them for a process, but AgreesWith and the port-collision checks
    /// would still see them and report a difference nobody configured.
    /// </summary>
    public IsolationSpec WithMode(string mode) =>
        string.Equals(mode, IsolationModes.Container, StringComparison.OrdinalIgnoreCase)
            ? this with { Mode = mode }
            : new IsolationSpec(mode);
}

/// <param name="ContainerPort">The port the application binds INSIDE the container.</param>
/// <param name="HostPort">The port to publish it on, or null to let the engine allocate one.</param>
/// <param name="Name">An optional label so an endpoint can be reported as something meaningful ("http", "grpc") rather than a bare number.</param>
public sealed record PortMapping(int ContainerPort, int? HostPort = null, string Protocol = "tcp", string? Name = null);

/// <summary>
/// Defined here beside RuntimeFlavors, for the same reason: the control plane validates against these,
/// the agent routes on them and the portal renders them, so all three must agree on the exact strings
/// without duplicating literals.
/// </summary>
public static class IsolationModes
{
    /// <summary>A child process of the agent, supervised via a Job Object on Windows.</summary>
    public const string Process = "process";

    /// <summary>A container running the generic enList runner image, with the package bind-mounted read-only. Requires a container engine on the agent's host.</summary>
    public const string Container = "container";

    public static readonly IReadOnlyList<string> All = [Process, Container];

    public static bool IsValid(string? mode) =>
        mode is not null && All.Any(m => string.Equals(m, mode, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A port enList actually published for a running application, as opposed to one merely requested.
/// Reported in the agent's status snapshot and rendered in the portal, so an operator can answer
/// "where is this listening right now?" from the same screen that says whether it is running.
///
/// This costs no control-plane migration: AgentReportEntity stores the snapshot as an opaque JSON
/// blob precisely so the agent's status shape can evolve independently (docs/03-architecture/SAD.md §5).
/// </summary>
/// <param name="Name">The PortMapping's label ("http", "grpc") when it had one — a bare number tells an operator nothing about what speaks there.</param>
/// <param name="HostPort">What the engine actually bound. For a dynamic mapping this is only knowable after the container starts, which is the whole reason resolved endpoints are reported separately from the request.</param>
/// <param name="HostAddress">Where to reach it. The agent's own address, not the container's — a container IP is unroutable from anywhere an operator is standing.</param>
public sealed record ResolvedEndpointDto(
    string? Name,
    string Protocol,
    int ContainerPort,
    int HostPort,
    string HostAddress);
