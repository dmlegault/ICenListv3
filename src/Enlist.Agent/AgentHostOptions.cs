namespace Enlist.Agent;

/// <summary>Tunables for AgentHost — bundled rather than a growing constructor parameter list. Every field has a sensible production default; tests override the timing-sensitive ones to run fast.</summary>
public sealed class AgentHostOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan StopGracePeriod { get; init; } = TimeSpan.FromSeconds(10);

    public int MaxRestartAttempts { get; init; } = 5;

    public TimeSpan RestartBaseDelay { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan RestartMaxDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long a run has to last before a subsequent crash is treated as a fresh problem rather than a continuation of the same crash streak.</summary>
    public TimeSpan RestartSettledDuration { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Log files (per-app and agent-level) older than this are deleted by the periodic retention sweep.</summary>
    public TimeSpan LogRetention { get; init; } = TimeSpan.FromDays(14);

    public TimeSpan LogRetentionSweepInterval { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Every status report otherwise only fires reactively (a service/job state change, a reconciliation pass) — a quiet-but-healthy agent with nothing changing would never send another one. This is the floor on how long that silence can run before a fresh "nothing changed, still here" snapshot goes out anyway, so a consumer watching report age (the portal's online/offline and stale indicators) can tell "quiet" apart from "actually gone." Comfortably under the portal's 5-minute staleness threshold so a couple of missed beats (a transient network blip) don't themselves cause a false stale reading.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromMinutes(2);

    public static AgentHostOptions Default => new();
}
