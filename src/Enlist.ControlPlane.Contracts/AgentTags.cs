namespace Enlist.ControlPlane.Contracts;

/// <summary>
/// What an agent's tags are, and whether a selector matches them. THE definition, not a copy of one:
/// the control plane resolves policy rules with it and the portal predicts that resolution with it,
/// so the two cannot disagree about which agents a rule affects.
///
/// They did disagree, and the way they disagreed is the point. The rule lived in
/// Enlist.ControlPlane/Program.cs with a comment asking the portal's copy to be kept in step by hand,
/// and the portal's copy carried the same request back. Neither noticed that both compared an agent
/// NAME case-sensitively while every other part of the system - the SQL collation, the bearer
/// handler's route check, the hub's group check - treats agent names as case-insensitive. A rule
/// targeting "web-07" therefore matched nothing at all on agent "WEB-07": no error, no warning, no
/// log line. The application simply never ran, and the rule looked perfectly correct on screen.
/// </summary>
public static class AgentTags
{
    /// <summary>
    /// The reserved key every agent carries without it ever being stored. "Target this one agent" is
    /// not a separate targeting mode on the wire - it is just the most specific possible selector,
    /// {"agent": name}.
    /// </summary>
    public const string SelfKey = "agent";

    /// <summary>
    /// An agent's stored tags plus its implicit self-tag. The synthesized value always wins over a
    /// same-named real tag: an operator setting an actual "agent" tag to something else would be
    /// confusing either way, and this keeps the self-tag authoritative rather than silently
    /// shadowable.
    /// </summary>
    public static Dictionary<string, string> Effective(string agentName, IReadOnlyDictionary<string, string>? storedTags)
    {
        var tags = storedTags is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(storedTags, StringComparer.Ordinal);

        tags[SelfKey] = agentName;
        return tags;
    }

    /// <summary>
    /// True when tags satisfies every pair in selector. An empty selector matches every agent, which
    /// is what "no tag filter" means.
    ///
    /// Ordinary tag keys and values are matched exactly: they are operator-chosen text, and deciding
    /// that "env=Prod" and "env=prod" are the same thing is a policy judgement nobody has made. The
    /// self-tag is the exception, and not by preference - its value is an agent NAME, and agent names
    /// are case-insensitive everywhere else in this system. Matching it exactly here made the control
    /// plane contradict its own database.
    /// </summary>
    public static bool SelectorMatches(IReadOnlyDictionary<string, string> tags, IReadOnlyDictionary<string, string> selector)
    {
        foreach (var (key, value) in selector)
        {
            if (!tags.TryGetValue(key, out var actual) || !string.Equals(actual, value, ComparisonFor(key)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when selector is EXACTLY the self-tag for this agent — used to describe a rule's scope, never to decide matching.</summary>
    public static bool IsSelfTargeted(string agentName, IReadOnlyDictionary<string, string> selector) =>
        selector.Count == 1 &&
        selector.TryGetValue(SelfKey, out var value) &&
        string.Equals(value, agentName, StringComparison.OrdinalIgnoreCase);

    private static StringComparison ComparisonFor(string key) =>
        string.Equals(key, SelfKey, StringComparison.OrdinalIgnoreCase)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
