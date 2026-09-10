using Enlist.ControlPlane.Contracts;

namespace Enlist.Portal.Services;

/// <summary>
/// The portal's own copy of the control plane's SelectorMatches/EffectiveAgentTags
/// (Enlist.ControlPlane/Program.cs) — duplicated rather than shared as a compiled reference because
/// the portal only has agent TAGS (AgentDto) to work with client-side, not database access, and the
/// two call sites that need this (ApplicationPolicyStateToggler's "who does this affect" confirmation,
/// and the Applications/Agents pages' own agent-name resolution) both need it as a pure, synchronous,
/// in-memory predicate over data they already fetched — not a server round trip. Keep this in sync
/// with the control plane's version if the matching rule ever changes.
///
/// Matches against the agent's stored Tags PLUS the same implicit self-tag ("agent" = the agent's own
/// Name) the control plane synthesizes at resolution time — AgentDto.Tags never contains this (it's
/// never actually stored), so a selector shaped like {"agent": "DEMO-01"} — what "target this one
/// agent" collapses down to — would otherwise silently match nothing here even though the server
/// resolves it correctly.
/// </summary>
public static class TagSelectorMatcher
{
    public static bool Matches(AgentDto agent, IReadOnlyDictionary<string, string> selector) =>
        selector.All(kv => EffectiveTagValue(agent, kv.Key) == kv.Value);

    public static List<string> ResolveAgentNames(IReadOnlyList<AgentDto> agents, IReadOnlyDictionary<string, string> selector) =>
        agents.Where(m => Matches(m, selector)).Select(m => m.Name).ToList();

    /// <summary>True when selector is EXACTLY {"agent": agent.Name} — "target this one agent" collapsed down to its most specific possible tag selector. Used only to decide how to describe/display a rule's scope; there's no separate targeting mode on the wire.</summary>
    public static bool IsSelfTargeted(AgentDto agent, IReadOnlyDictionary<string, string> selector) =>
        selector.Count == 1 && selector.TryGetValue("agent", out var value) && value == agent.Name;

    /// <summary>Human-readable summary of what a selector actually matches, from one specific agent's point of view — "this agent only" / "every agent" (an empty selector) / the raw "tag: k=v,..." pairs otherwise. Shared by every screen that shows a policy rule's blast radius (Applications' running-instances table, Agents' read-only table, the policy screen's Target column) so the wording can't drift between them.</summary>
    public static string DescribeScope(AgentDto agent, IReadOnlyDictionary<string, string> selector) =>
        IsSelfTargeted(agent, selector) ? "this agent only" :
        selector.Count == 0 ? "every agent" :
        $"tag: {string.Join(",", selector.Select(kv => $"{kv.Key}={kv.Value}"))}";

    private static string? EffectiveTagValue(AgentDto agent, string key) =>
        key == "agent" ? agent.Name : agent.Tags.GetValueOrDefault(key);
}
