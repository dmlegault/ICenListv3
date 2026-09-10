using System.Reflection;

namespace Enlist.Runner.Discovery;

public sealed record DiscoveredJob(
    string Name,
    string? Description,
    /// <summary>
    /// The cron expression compiled into [EnlistJob], if any. This is a DEFAULT, never authoritative:
    /// the agent owns scheduling (see docs/03-architecture/enList-v3-Design.md section 2 — the runner has no notion
    /// of time or schedule at all, deliberately, so it never needs a cron-parsing package). A
    /// deployment- or runtime-level override of a job's schedule is expected to live in the agent's
    /// assignment/manifest data and take precedence over this value; the runner reports it purely as
    /// "what the plugin shipped with" so the agent has something to fall back to before any override
    /// is configured.
    /// </summary>
    string? DeclaredCron,
    Type Type,
    MethodInfo? ExecuteMethod)
{
    public string TypeName => Type.FullName ?? Type.Name;
}
