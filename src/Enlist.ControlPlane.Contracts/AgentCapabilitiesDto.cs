namespace Enlist.ControlPlane.Contracts;

/// <summary>
/// What an agent can actually do right now, as reported by the agent itself.
///
/// Deliberately separate from the status snapshot: that blob is stored opaquely by the control plane
/// (see AgentReportEntity.SnapshotJson) precisely so the agent's status shape can evolve with no
/// server-side migration, and parsing it to answer "can this agent run containers?" would spend that
/// property for a UI chip. Capability is registry metadata about the agent, not a reading about its
/// applications, so it belongs on the agent row.
/// </summary>
/// <param name="IsolationModes">The isolation modes this agent is CONFIGURED for — always contains "process".</param>
/// <param name="ContainerEngine">Present only when the agent was configured for containers. See ContainerEngineStatus for why configuration alone is not the answer.</param>
/// <param name="PushChannel">Whether the agent's push connection to the control plane is delivering — one of <see cref="PushChannelStates"/>. Heartbeats and status reports travel over HTTP and keep arriving while that connection is down, so "online" alone cannot say whether a policy change will reach the agent promptly; this can. Null from an agent older than this field.</param>
public sealed record AgentCapabilitiesDto(
    IReadOnlyList<string> IsolationModes,
    ContainerEngineStatus? ContainerEngine = null,
    string? PushChannel = null);

/// <summary>
/// Values of <see cref="AgentCapabilitiesDto.PushChannel"/>. Strings rather than an enum so an older
/// reader never fails on a newer value — the same reasoning as RuntimeFlavors and IsolationModes.
/// </summary>
public static class PushChannelStates
{
    /// <summary>Connected and joined to the agent's group: a push will arrive.</summary>
    public const string Connected = "connected";

    /// <summary>The connection dropped and the agent is retrying. A policy change waits for the periodic re-fetch or the reconnect, whichever comes first.</summary>
    public const string Reconnecting = "reconnecting";

    /// <summary>Never connected, or closed outright and being reopened. Same consequence as reconnecting.</summary>
    public const string Disconnected = "disconnected";
}

/// <summary>
/// Whether a container engine is genuinely usable, not merely asked for.
///
/// The distinction is the whole point. Being started with --container-image proves only that someone
/// passed a flag; it does not prove Docker is installed, that its daemon is running, or that the agent
/// can reach it. An agent in that state will accept a container policy and then fail it, so reporting
/// "container-capable" from configuration alone would make the portal actively misleading.
///
/// Hence three states rather than a boolean — "configured but unreachable" is a real, common and
/// otherwise baffling condition, and it is the one worth showing loudest.
/// </summary>
/// <param name="Available">True only if the engine answered a version query.</param>
/// <param name="Version">The engine's server version when reachable.</param>
/// <param name="Error">Why it isn't reachable, in the engine's own words — an operator needs the reason, not just the verdict.</param>
/// <param name="Image">The runner image this agent would use.</param>
/// <param name="CheckedAtUtc">When the probe last ran. Re-run on the heartbeat, because a daemon up at startup can be down later.</param>
/// <param name="Engine">Which engine — "docker" or "wslc" (WSL containers). Null from an agent older than this field.</param>
public sealed record ContainerEngineStatus(
    bool Available,
    string? Version,
    string? Error,
    string? Image,
    DateTimeOffset CheckedAtUtc,
    string? Engine = null);

/// <summary>Replaces the agent's reported capabilities wholesale — same "not a merge" contract as SetAgentTagsRequest.</summary>
public sealed record SetAgentCapabilitiesRequest(AgentCapabilitiesDto Capabilities);
