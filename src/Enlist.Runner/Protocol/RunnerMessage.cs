using System.Text.Json.Serialization;

namespace Enlist.Runner.Protocol;

/// <summary>
/// Messages the runner sends UP the pipe to whatever is on the other end — the agent in production,
/// a stub test harness in isolation (see docs/03-architecture/enList-v3-Design.md section 11: "testable standalone
/// with a stub driver on the other end of the pipe"). This type and everything under it belongs only
/// to the runner<->agent boundary, never to the plugin contract, so — unlike EnlistAttributes.cs —
/// there is no objection to it being a normal compiled type a future Enlist.Agent project references
/// directly, or a stub harness reproduces from this same source file.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ReadyMessage), "ready")]
[JsonDerivedType(typeof(LogMessage), "log")]
[JsonDerivedType(typeof(StateChangedMessage), "stateChanged")]
[JsonDerivedType(typeof(JobResultMessage), "jobResult")]
[JsonDerivedType(typeof(FaultedMessage), "faulted")]
public abstract record RunnerMessage;

/// <summary>
/// Sent once, after discovery, before the runner accepts any command.
///
/// ProtocolVersion defaults to RunnerProtocol.Unreported (0) rather than to the current version on
/// purpose: a runner too old to send the field at all must be DISTINGUISHABLE from a current one, and
/// System.Text.Json fills a missing property from the record parameter's default. Defaulting it to the
/// current version would make every ancient runner claim to be current — exactly the silent
/// misinterpretation the version check exists to prevent. The runner always passes it explicitly; see
/// RunnerHost.RunAsync.
/// </summary>
public sealed record ReadyMessage(
    IReadOnlyList<ServiceInfo> Services,
    IReadOnlyList<JobInfo> Jobs,
    IReadOnlyList<string> Warnings,
    IsolationInfo Isolation,
    string? ApplicationDescription = null,
    int ProtocolVersion = RunnerProtocol.Unreported) : RunnerMessage;

public sealed record ServiceInfo(string Name, string? Description, string TypeName);

/// <summary>
/// Whether PluginLoadContext isolation actually engaged for this application — reported explicitly
/// because a silently-not-engaged isolation looks identical to a working one until two plugin
/// versions actually collide. enList v2's README calls this out directly: "no exception, nothing in
/// the log... the collision then reappears far from its cause, looking like a regression somewhere
/// else." ResolverCount == 0 is expected and healthy for a plugin with no private dependencies (it
/// has nothing to resolve privately); DepsFilesFound == 0 is the actual failure signal.
/// </summary>
public sealed record IsolationInfo(int DepsFilesFound, int ResolverCount, int PrivateResolutionCount, IReadOnlyList<string> OrphanedDeps);

/// <summary>
/// DeclaredCron is the attribute's compiled-in value, reported so the agent has a default before any
/// deployment- or runtime-level override is configured. It is never the runner acting on a schedule —
/// the runner has no scheduling logic at all, on purpose (see DiscoveredJob.DeclaredCron).
/// </summary>
public sealed record JobInfo(string Name, string? Description, string TypeName, string? DeclaredCron);

public enum LogLevel { Trace, Debug, Information, Warning, Error, Critical }

/// <summary>Source is "runner" for the process's own lifecycle logging, or a service/job Name for output attributed to it.</summary>
public sealed record LogMessage(string Source, LogLevel Level, string Text, DateTimeOffset TimestampUtc) : RunnerMessage;

public enum RunnerTargetKind { Service, Job }

public enum RunnerState { Starting, Running, Stopping, Stopped, Faulted }

public sealed record StateChangedMessage(string Target, RunnerTargetKind TargetKind, RunnerState State) : RunnerMessage;

/// <summary>
/// How a job run ended. Three states rather than a bool because a run that was deliberately stopped
/// is neither of the other two: nothing broke, and it also did not finish its work. Collapsing
/// cancellation into failure reports an operator's own Stop back to them as something going wrong.
/// </summary>
public enum JobOutcome { Succeeded, Failed, Cancelled }

/// <summary>Error carries the reason for Failed and Cancelled, and is null for Succeeded.</summary>
public sealed record JobResultMessage(string Job, string RunId, JobOutcome Outcome, string? Error, long DurationMs) : RunnerMessage;

/// <summary>Target is null for a runner-wide fault (e.g. discovery itself threw) rather than one attributable to a single service/job.</summary>
public sealed record FaultedMessage(string? Target, string Error) : RunnerMessage;
