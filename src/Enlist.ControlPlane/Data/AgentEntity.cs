namespace Enlist.ControlPlane.Data;

/// <summary>
/// One row per agent that's ever been heard from, or been pre-registered with tags before it ever
/// connects. LastSeenUtc is updated on every policy-fetch and status report — the cheapest possible
/// liveness signal, since a real agent calls both constantly by construction, no separate heartbeat
/// protocol needed.
/// </summary>
public sealed class AgentEntity
{
    public required string Name { get; set; }

    /// <summary>role=web, env=prod, region=east, etc. — what ApplicationPolicyEntity.TagSelectorJson matches against. Every agent also implicitly matches a selector naming its own Name, even though that isn't stored here — see the tag-only targeting resolution in Program.cs.</summary>
    public string TagsJson { get; set; } = "{}";

    public DateTimeOffset FirstSeenUtc { get; set; }

    public DateTimeOffset LastSeenUtc { get; set; }

    /// <summary>Included by default. False means this agent qualifies for NOTHING regardless of what its tags would otherwise match — the one-click "stop everything on this box" control (Agents.razor's Scheduling toggle). Enforced entirely at resolution time (see ResolveMatchingPoliciesForAgentAsync in Program.cs), not by touching any policy row, so re-including makes the agent immediately eligible again for whatever already targets it.</summary>
    public bool SchedulingEnabled { get; set; } = true;

    /// <summary>A serialized AgentCapabilitiesDto, last reported by the agent itself (PUT /api/agents/{name}/capabilities). Null for an agent that has never reported, or one running a build older than this concept. Stored as JSON for the same reason as TagsJson: read and written whole, never queried by key.</summary>
    public string? CapabilitiesJson { get; set; }
}
