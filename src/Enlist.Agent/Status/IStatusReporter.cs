namespace Enlist.Agent.Status;

/// <summary>Where AgentHost sends its status snapshot beyond the local status.json — nothing, by default (NullStatusReporter), or the control plane when one is configured.</summary>
public interface IStatusReporter
{
    Task ReportAsync(AgentStatusSnapshot snapshot, CancellationToken ct);

    /// <summary>
    /// Reports what this agent can do, as opposed to what it is currently doing. Default no-op so a
    /// reporter that only cares about status needs no change.
    ///
    /// Sent on the same cadence as the status snapshot rather than once at startup: the container
    /// engine probe behind it can go from reachable to unreachable while the agent runs, and an agent
    /// still advertising a dead engine is exactly the misleading state this reporting exists to avoid.
    /// </summary>
    Task ReportCapabilitiesAsync(Enlist.ControlPlane.Contracts.AgentCapabilitiesDto capabilities, CancellationToken ct) => Task.CompletedTask;
}

public sealed class NullStatusReporter : IStatusReporter
{
    public static readonly NullStatusReporter Instance = new();

    public Task ReportAsync(AgentStatusSnapshot snapshot, CancellationToken ct) => Task.CompletedTask;
}
