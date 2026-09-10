namespace Enlist.ControlPlane.Contracts;

public sealed record AgentDto(
    string Name,
    IReadOnlyDictionary<string, string> Tags,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    bool SchedulingEnabled = true,

    /// <summary>Null until this agent has reported in at least once since the capability endpoint existed — an older agent, or one that has never connected.</summary>
    AgentCapabilitiesDto? Capabilities = null);

/// <summary>Replaces the full tag set — not a merge. Simpler contract, and matches how CronOverrides updates already work.</summary>
public sealed record SetAgentTagsRequest(IReadOnlyDictionary<string, string> Tags);

/// <summary>False excludes this agent from scheduling entirely — it stops running everything it currently hosts and won't pick up any policy rule (even one that already matches it) until re-included. See AgentEntity.SchedulingEnabled for why this needs no agent-side code at all.</summary>
public sealed record SetAgentSchedulingRequest(bool SchedulingEnabled);
