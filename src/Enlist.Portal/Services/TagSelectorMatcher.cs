using Enlist.ControlPlane.Contracts;

namespace Enlist.Portal.Services;

/// <summary>
/// The portal's view over AgentDto of the ONE matching rule, which lives in
/// Enlist.ControlPlane.Contracts.AgentTags. This used to be a hand-kept copy of the control plane's
/// version, with a comment on each asking the other to be updated in step; both then compared an
/// agent name case-sensitively while the rest of the system did not, and a rule targeting "web-07"
/// matched nothing on "WEB-07" in either place. Nothing here decides anything any more - it adapts
/// AgentDto to the shared predicate and adds the wording the screens use.
/// </summary>
public static class TagSelectorMatcher
{
    public static bool Matches(AgentDto agent, IReadOnlyDictionary<string, string> selector) =>
        AgentTags.SelectorMatches(AgentTags.Effective(agent.Name, agent.Tags), selector);

    public static List<string> ResolveAgentNames(IReadOnlyList<AgentDto> agents, IReadOnlyDictionary<string, string> selector) =>
        agents.Where(a => Matches(a, selector)).Select(a => a.Name).ToList();

    /// <summary>True when selector is EXACTLY the self-tag for this agent — "target this one agent" collapsed down to its most specific possible tag selector. Used only to decide how to describe a rule's scope; there's no separate targeting mode on the wire.</summary>
    public static bool IsSelfTargeted(AgentDto agent, IReadOnlyDictionary<string, string> selector) =>
        AgentTags.IsSelfTargeted(agent.Name, selector);

    /// <summary>Human-readable summary of what a selector actually matches, from one specific agent's point of view — "this agent only" / "every agent" (an empty selector) / the raw "tag: k=v,..." pairs otherwise. Shared by every screen that shows a policy rule's blast radius (Applications' running-instances table, Agents' read-only table, the policy screen's Target column) so the wording can't drift between them.</summary>
    public static string DescribeScope(AgentDto agent, IReadOnlyDictionary<string, string> selector) =>
        IsSelfTargeted(agent, selector) ? "this agent only" :
        selector.Count == 0 ? "every agent" :
        $"tag: {string.Join(",", selector.Select(kv => $"{kv.Key}={kv.Value}"))}";
}
