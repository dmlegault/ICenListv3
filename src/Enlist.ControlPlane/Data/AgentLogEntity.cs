namespace Enlist.ControlPlane.Data;

/// <summary>One forwarded application log line. Normalized (unlike AgentReportEntity's opaque JSON blob) because the portal's log-tail page pages through these by Id — something a shared shape needs to agree on, not something either side benefits from being free to evolve independently.</summary>
public sealed class AgentLogEntity
{
    public long Id { get; set; }

    public required string AgentName { get; set; }

    public required string ApplicationName { get; set; }

    public required string Level { get; set; }

    public required string Source { get; set; }

    public required string Text { get; set; }

    public DateTimeOffset TimestampUtc { get; set; }
}
