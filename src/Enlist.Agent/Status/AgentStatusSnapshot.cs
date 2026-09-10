namespace Enlist.Agent.Status;

/// <summary>What's actually running right now, written to the agent's Logs/status.json so that answering "what's running" doesn't require reading raw log files by hand.</summary>
public sealed record AgentStatusSnapshot(DateTimeOffset GeneratedAtUtc, IReadOnlyList<ApplicationStatusEntry> Applications);

public sealed record ApplicationStatusEntry(
    string Name,
    string State,
    int? Pid,
    int RestartCount,
    IReadOnlyList<ServiceStatusEntry> Services,
    IReadOnlyList<JobStatusEntry> Jobs,
    string? Description = null,

    // Appended, never reordered — the portal deserializes these by name but several call sites
    // construct them positionally.

    /// <summary>"process" or "container" (IsolationModes). Lets the portal say HOW an application is running, not just that it is.</summary>
    string? IsolationMode = null,

    /// <summary>Process id or container id — whatever the backend that started it can actually be asked about. See IRunnerInstance.RuntimeId, and note Pid above is 0 for a container.</summary>
    string? RuntimeId = null,

    /// <summary>Ports enList published for this application, empty for a process (which binds host ports itself, outside enList's knowledge). Lets the portal answer "where is this listening right now?".</summary>
    IReadOnlyList<Enlist.ControlPlane.Contracts.ResolvedEndpointDto>? Endpoints = null);

public sealed record ServiceStatusEntry(string Name, string State, string? Description = null);

/// <summary>
/// Cron is the currently EFFECTIVE schedule (the assignment's override if one is set, otherwise the
/// job's own declared default) — see AgentHost's JobTrackedState.Cron, which this is read straight
/// from. Lets a portal editing an assignment's cron overrides show "what it's currently set to"
/// instead of asking the operator to guess.
///
/// LastRunOutcome is a JobRunOutcome value, or null when the job has not run since this agent started.
/// </summary>
public sealed record JobStatusEntry(string Name, DateTimeOffset? LastRunUtc, string? LastRunOutcome, bool Paused = false, string? Cron = null, string? Description = null);

/// <summary>
/// How a job's most recent run ended. Mirrors the runner protocol's JobOutcome, as strings rather
/// than an enum because this snapshot crosses to the control plane and the portal as JSON, where a
/// numeric enum would be unreadable in a stored SnapshotJson blob and would silently shift meaning if
/// a member were ever inserted.
///
/// Cancelled is deliberately its own value rather than a flavour of Failed: an operator who pressed
/// Stop should not be told their job broke.
/// </summary>
public static class JobRunOutcome
{
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}

public static class ApplicationState
{
    public const string Starting = "Starting";
    public const string Running = "Running";
    public const string Restarting = "Restarting";
    public const string Failed = "Failed";
    public const string Stopped = "Stopped";
}
