using Enlist.ControlPlane.Contracts;

namespace Enlist.Portal.Services;

/// <summary>
/// The one place this app decides whether two or more policy rules "agree" — mirrors the control
/// plane's own resolution (see ApplicationPolicyDto.ConflictReason): rules matching the same agent
/// disagree if they differ on Path, PackageDigest, DesiredState, CronOverrides or Isolation. Shared by
/// RunningInstancesTable and AgentRunningApplicationsTable (labeling a live row that's actually in this
/// state, from the Applications and Agents tabs respectively), ApplicationPolicyWizardDialog (warning
/// before a NEW rule would create this state), and ApplicationPolicyScreen (flagging an EXISTING rule
/// already in it). Every surface calls this rather than keeping a private copy: the one private copy
/// that existed never learned about Isolation, so the Agents tab stayed quiet on a conflict the other
/// surfaces flagged.
/// </summary>
public static class PolicyConflictDetector
{
    public static bool Agree(IReadOnlyCollection<ApplicationPolicyDto> policies)
    {
        if (policies.Count <= 1)
        {
            return true;
        }

        var first = policies.First();
        return policies.Skip(1).All(p => AgreesWith(first, p));
    }

    /// <summary>
    /// A field-by-field mirror of the control plane's ResolveEffectivePoliciesForAgentAsync, which is
    /// the authority; this is the portal predicting what the server will decide, so every comparison
    /// here matches the server's EXACTLY - ordinal on the four scalar fields, and IsolationSpec's own
    /// AgreesWith for the rest.
    ///
    /// It used to be a hand-built canonical STRING instead, and the string quietly disagreed with the
    /// authority in both directions: it dropped PortMapping.Name, which the server compares, so the
    /// portal accepted rules the server rejected; and it compared Mode, Image and Networks
    /// case-sensitively, which the server does not, so the portal flagged conflicts the server was
    /// perfectly happy with. Calling the shared predicate is what makes "mirrors the control plane"
    /// true rather than aspirational.
    /// </summary>
    private static bool AgreesWith(ApplicationPolicyDto a, ApplicationPolicyDto b) =>
        a.Path == b.Path &&
        a.PackageDigest == b.PackageDigest &&
        a.DesiredState == b.DesiredState &&
        CronOverridesAgree(a.CronOverrides, b.CronOverrides) &&
        (a.Isolation ?? IsolationSpec.ProcessDefault).AgreesWith(b.Isolation ?? IsolationSpec.ProcessDefault);

    private static bool CronOverridesAgree(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var other) && other == kv.Value);

    /// <summary>Every currently-registered agent that's matched by more than one of `policies`, where
    /// those matching rules disagree — keyed by agent name, valued by the disagreeing rules themselves.
    /// An agent matched by only one rule (or by several that all agree) never appears here.</summary>
    public static Dictionary<string, List<ApplicationPolicyDto>> FindConflictsByAgent(
        IReadOnlyList<AgentDto> agents, IReadOnlyList<ApplicationPolicyDto> policies)
    {
        var result = new Dictionary<string, List<ApplicationPolicyDto>>(StringComparer.OrdinalIgnoreCase);

        foreach (var agent in agents)
        {
            var matching = policies.Where(p => TagSelectorMatcher.Matches(agent, p.TagSelector)).ToList();
            if (matching.Count > 1 && !Agree(matching))
            {
                result[agent.Name] = matching;
            }
        }

        return result;
    }

    /// <summary>
    /// Static host ports a proposed rule would fight over, per agent — the cross-application conflict
    /// class <see cref="Agree"/> cannot see, because it only ever compares rules for the SAME
    /// application and this collision happens between different ones.
    ///
    /// Mirrors the control plane's ApplyPortCollisions, which remains the authority; this is the portal
    /// warning BEFORE a rule is saved rather than discovering it at resolution afterwards.
    ///
    /// Only static ports are considered. A null HostPort means "let the engine allocate", which cannot
    /// collide by construction — and treating it as a conflict would condemn the very configuration that
    /// makes a tag-selector rule work across many agents.
    /// </summary>
    /// <param name="candidateAgentNames">Agents the proposed rule currently matches — passed in rather than re-derived, since the caller has already resolved them for its own preview.</param>
    public static List<PortCollision> FindPortCollisions(
        IReadOnlyList<AgentDto> agents,
        IReadOnlyCollection<string> candidateAgentNames,
        string candidateApplication,
        IsolationSpec? candidateIsolation,
        IReadOnlyList<ApplicationPolicyDto> allPolicies)
    {
        var wanted = (candidateIsolation?.Ports ?? [])
            .Where(p => p.HostPort is not null)
            .Select(p => (Port: p.HostPort!.Value, p.Protocol))
            .ToList();

        if (wanted.Count == 0)
        {
            return [];
        }

        var collisions = new List<PortCollision>();

        foreach (var agentName in candidateAgentNames)
        {
            var agent = agents.FirstOrDefault(a => string.Equals(a.Name, agentName, StringComparison.OrdinalIgnoreCase));
            if (agent is null)
            {
                continue;
            }

            foreach (var other in allPolicies)
            {
                // A rule for the SAME application is never a port collision — it is either the rule
                // being edited, or an ordinary same-application disagreement Agree already covers.
                if (string.Equals(other.ApplicationName, candidateApplication, StringComparison.OrdinalIgnoreCase) ||
                    !TagSelectorMatcher.Matches(agent, other.TagSelector))
                {
                    continue;
                }

                foreach (var port in other.Isolation?.Ports ?? [])
                {
                    if (port.HostPort is { } hostPort &&
                        wanted.Any(w => w.Port == hostPort && string.Equals(w.Protocol, port.Protocol, StringComparison.OrdinalIgnoreCase)))
                    {
                        collisions.Add(new PortCollision(agentName, hostPort, port.Protocol, other.ApplicationName));
                    }
                }
            }
        }

        return collisions.Distinct().ToList();
    }

    /// <summary>
    /// Port collisions affecting one application's existing rules, keyed by agent — the reactive
    /// counterpart to <see cref="FindPortCollisions"/>, which answers the same question for a rule that
    /// has not been saved yet.
    ///
    /// This exists because <see cref="FindConflictsByAgent"/> structurally cannot find these. That
    /// method groups by application and asks whether rules for the SAME application disagree; a static
    /// port collision is between DIFFERENT applications, so it falls between the groups and is invisible
    /// to every surface built on it — the wizard would warn about port collisions while the policy screen
    /// and the Applications tab, both built on FindConflictsByAgent, showed nothing.
    /// </summary>
    public static Dictionary<string, List<PortCollision>> FindPortCollisionsByAgent(
        IReadOnlyList<AgentDto> agents,
        string applicationName,
        IReadOnlyList<ApplicationPolicyDto> allPolicies)
    {
        var result = new Dictionary<string, List<PortCollision>>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in allPolicies.Where(p => string.Equals(p.ApplicationName, applicationName, StringComparison.OrdinalIgnoreCase)))
        {
            var matchingAgents = agents
                .Where(a => TagSelectorMatcher.Matches(a, rule.TagSelector))
                .Select(a => a.Name)
                .ToList();

            foreach (var collision in FindPortCollisions(agents, matchingAgents, applicationName, rule.Isolation, allPolicies))
            {
                if (!result.TryGetValue(collision.AgentName, out var forAgent))
                {
                    result[collision.AgentName] = forAgent = [];
                }

                if (!forAgent.Contains(collision))
                {
                    forAgent.Add(collision);
                }
            }
        }

        return result;
    }
}

/// <summary>
/// One agent on which a proposed rule would fight an existing rule for the same host port.
/// </summary>
/// <param name="AgentName">The agent where both rules land — a host port is only scarce within one host.</param>
/// <param name="OtherApplication">Who already claims it. Naming the other side is the whole value: the engine's own error names nobody.</param>
public sealed record PortCollision(string AgentName, int HostPort, string Protocol, string OtherApplication);
