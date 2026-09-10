using System.Text.Json.Serialization;

namespace Enlist.Runner.Legacy.Protocol;

/// <summary>
/// Messages the runner sends UP the pipe to whatever is on the other end (the agent, in production).
/// Wire-identical to the modern Enlist.Runner's own RunnerMessage — same JSON shape, same "kind"
/// discriminator values — so Enlist.Agent's MessageChannel-consuming side needs no changes at all to
/// talk to this runner instead. Duplicated here as source (not referenced from Enlist.Runner) for the
/// same reason contracts/EnlistAttributes.cs is source, not a package: this project has its own
/// zero-plugin-visible-dependency posture to keep, independent of Enlist.Runner's build.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ReadyMessage), "ready")]
[JsonDerivedType(typeof(LogMessage), "log")]
[JsonDerivedType(typeof(StateChangedMessage), "stateChanged")]
[JsonDerivedType(typeof(JobResultMessage), "jobResult")]
[JsonDerivedType(typeof(FaultedMessage), "faulted")]
public abstract record RunnerMessage;

/// <summary>Sent once, after discovery, before the runner accepts any command.</summary>
/// <summary>
/// ProtocolVersion must stay wire-identical to the modern Enlist.Runner's ReadyMessage, including the
/// Unreported default — an agent uses it to detect a runner built against a different contract, and a
/// runner that omits it is refused outright.
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
/// Whether isolation actually engaged for this application. Unlike the modern runner's
/// PluginLoadContext (AssemblyLoadContext-based, per-application dependency resolution),
/// LegacyPluginLoadContext provides no real isolation in this first cut — see its own doc comment.
/// This runner always reports DepsFilesFound=0/ResolverCount=0/PrivateResolutionCount=0/no orphans,
/// honestly, rather than fabricating isolation metrics that don't apply to how it actually loads
/// plugins. AgentHost's existing "isolation did not engage" log line will therefore always fire for a
/// legacy-flavored application — accurate, if worded with the modern runner's deps.json convention in
/// mind, which doesn't apply here.
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
