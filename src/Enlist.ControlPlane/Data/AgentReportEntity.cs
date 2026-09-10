namespace Enlist.ControlPlane.Data;

/// <summary>
/// One row per status report an agent posts. Stored as an opaque JSON blob rather than normalized
/// into per-service/per-job tables — this control plane doesn't need to query into the snapshot's
/// internals yet (it just needs "what did this agent last report, and when"), and Enlist.Agent's
/// AgentStatusSnapshot shape can keep evolving without a migration here every time it does.
/// </summary>
public sealed class AgentReportEntity
{
    public long Id { get; set; }

    public required string AgentName { get; set; }

    public required string SnapshotJson { get; set; }

    public DateTimeOffset ReportedAtUtc { get; set; }
}
