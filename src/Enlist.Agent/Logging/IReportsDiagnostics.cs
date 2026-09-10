namespace Enlist.Agent.Logging;

/// <summary>
/// A best-effort component that has something an operator should read — a failure, a recovery, a
/// change of state — and no sink of its own. AgentHost subscribes every component it is handed that
/// implements this and writes the lines to the agent log, so the reporter, the forwarder and the
/// assignment source are constructed in Program.cs without knowing where the log lives.
/// </summary>
public interface IReportsDiagnostics
{
    event Action<string>? Diagnostic;
}
