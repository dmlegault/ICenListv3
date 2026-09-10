using System.Text.Json.Serialization;

namespace Enlist.Runner.Legacy.Protocol;

/// <summary>Commands the runner reads DOWN the pipe. See RunnerMessage.cs for the boundary this sits on — wire-identical to the modern Enlist.Runner's own AgentCommand.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(StartServiceCommand), "startService")]
[JsonDerivedType(typeof(StopServiceCommand), "stopService")]
[JsonDerivedType(typeof(RunJobCommand), "runJob")]
[JsonDerivedType(typeof(CancelJobCommand), "cancelJob")]
[JsonDerivedType(typeof(ShutdownCommand), "shutdown")]
public abstract record AgentCommand;

public sealed record StartServiceCommand(string Service) : AgentCommand;

/// <summary>
/// TimeoutMs is advisory to the runner (it logs a warning if Stop() overruns it) — the actual
/// eviction guarantee is the agent killing this process if it doesn't exit in time, per design doc
/// section 3 ("the one capability that justified Aware's C++ host is obtained here by process
/// topology alone"). The runner never kills itself over this timeout.
/// </summary>
public sealed record StopServiceCommand(string Service, int TimeoutMs) : AgentCommand;

public sealed record RunJobCommand(string Job, string RunId, IReadOnlyDictionary<string, string> Settings) : AgentCommand;

/// <summary>
/// Cancel every in-flight run of one job, by cancelling the CancellationToken its [EnlistExecute]
/// was handed. The runner cannot stop the work itself — it can only signal — so a job that never
/// observes its token keeps running until it returns on its own.
///
/// Distinct from the agent's scheduler-level stop, which unregisters the job so it stops FIRING and
/// never reaches a runner. Stopping a job in the portal does both: no more firings, and the run
/// currently underway is asked to unwind.
/// </summary>
public sealed record CancelJobCommand(string Job) : AgentCommand;

/// <summary>Stop every running service, then exit. The runner does not distinguish graceful vs. forced beyond this — a hard kill is the agent's job, not a message on the wire.</summary>
public sealed record ShutdownCommand(int TimeoutMs) : AgentCommand;
