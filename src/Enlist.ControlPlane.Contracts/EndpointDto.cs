namespace Enlist.ControlPlane.Contracts;

/// <summary>
/// One place an application is listening right now, fleet-wide — the projection a reverse proxy polls
/// (docs/03-architecture/Container-Story.md §8.4, level 3).
///
/// This is enList reporting facts, not routing. There is no hostname, no TLS, no path rule and no
/// health policy here, because those belong to the proxy and enList would be reimplementing Kubernetes
/// Ingress badly if it grew them. What enList uniquely knows — and a proxy cannot discover on its own —
/// is which agents are currently running which application, and on which port the engine actually
/// published it.
/// </summary>
/// <param name="Stale">The reporting agent has not checked in within the liveness threshold, so this is a last-known value rather than a confirmed one. Surfaced rather than silently dropped: "an agent went quiet" and "an application stopped" are different events and an operator needs to tell them apart. Proxy-facing projections exclude these.</param>
/// <param name="ReportedAtUtc">When the agent generated the snapshot this came from — lets a consumer apply its own freshness policy instead of inheriting enList's.</param>
public sealed record EndpointDto(
    string ApplicationName,
    string AgentName,
    string? Name,
    string Protocol,
    int ContainerPort,
    int HostPort,
    string HostAddress,
    bool Stale,
    DateTimeOffset ReportedAtUtc);
