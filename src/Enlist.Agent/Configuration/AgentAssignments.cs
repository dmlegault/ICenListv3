using Enlist.ControlPlane.Contracts;

namespace Enlist.Agent.Configuration;

/// <summary>
/// The step-2 stand-in for the control plane's eventual Assignments table (design doc section 5) —
/// "reads assignments from a local file" per section 11's sequencing plan. Deliberately shaped like
/// that future table already (ApplicationId/DesiredState/etc. by another name) so the control plane swaps the
/// SOURCE of assignments without changing what an assignment IS.
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

    /// <summary>Non-null when the control plane found two or more enabled policy rules matching this agent for this application that disagree on what should run (see ApplicationPolicyDto.ConflictReason) — Path/PackageDigest are meaningless in this case (there was no single winner to resolve them from). AgentHost.StartApplicationAsync checks this before anything else and goes straight to Failed, the same "configuration problem, not transient" posture as a missing runtime flavor.</summary>
    public string? ConflictReason { get; set; }
}

public enum DesiredState { Running, Stopped }
