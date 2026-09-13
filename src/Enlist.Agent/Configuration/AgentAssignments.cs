using Enlist.ControlPlane.Contracts;

namespace Enlist.Agent.Configuration;

/// <summary>
/// What one agent has been told to run, resolved: one entry per application, with the winning path or
/// package already chosen. This is the agent's OWN vocabulary and the one place the word "assignment"
/// survives on purpose — the control plane calls its rows application policy RULES, several of which
/// can target one agent and have to be resolved into at most one outcome per application before the
/// agent can act on them.
///
/// It reaches the agent from either of two sources, and the shape is identical from both
/// (IAssignmentSource): the control plane, or a local JSON file via --assignments. The file mode
/// came first and is still how the agent runs with no control plane at all.
/// </summary>
public sealed class AgentAssignments
{
    public List<ApplicationAssignment> Applications { get; set; } = new();
}

public sealed class ApplicationAssignment
{
    /// <summary>Also the process image name staged for this application's runner — see RunnerStaging.</summary>
    public required string Name { get; set; }

    /// <summary>Directory containing the application's plugin DLLs — passed to enlist-runner as --app.</summary>
    public required string Path { get; set; }

    public DesiredState DesiredState { get; set; } = DesiredState.Running;

    /// <summary>
    /// Job name -> cron expression. Overrides [EnlistJob]'s compiled-in Cron for that job. This is the
    /// "deployment- or runtime-level override" DiscoveredJob.DeclaredCron's doc comment anticipates —
    /// the agent is where that override actually lives, matched on the job Name the runner reports in
    /// its Ready message.
    /// </summary>
    public Dictionary<string, string> CronOverrides { get; set; } = new();

    /// <summary>Which enlist-runner build this application needs — see RuntimeFlavors. AgentHost picks the matching entry from its own configured runner-bin directories; an assignment naming a flavor the agent has no path for cannot be started.</summary>
    public string RuntimeFlavor { get; set; } = RuntimeFlavors.Default;

    /// <summary>HOW this runs on this agent — in-process or in a container. Comes straight from the resolved policy rule (ApplicationPolicyDto.Isolation); never null here, because a rule with no opinion resolves to IsolationSpec.ProcessDefault, which is exactly what every rule meant before the concept existed. AgentHost picks a runner backend from Mode the same way it picks a runner-bin from RuntimeFlavor.</summary>
    public IsolationSpec Isolation { get; set; } = IsolationSpec.ProcessDefault;

    /// <summary>Non-null when the control plane found two or more policy rules matching this agent for this application that disagree on what should run (see ApplicationPolicyDto.ConflictReason) — Path/PackageDigest are meaningless in this case (there was no single winner to resolve them from). AgentHost.StartApplicationAsync checks this before anything else and goes straight to Failed, the same "configuration problem, not transient" posture as a missing runtime flavor.</summary>
    public string? ConflictReason { get; set; }
}

public enum DesiredState { Running, Stopped }
