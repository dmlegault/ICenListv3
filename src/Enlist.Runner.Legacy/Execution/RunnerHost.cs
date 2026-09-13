using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Channels;

using Enlist.Runner.Legacy.Discovery;
using Enlist.Runner.Legacy.Logging;
using Enlist.Runner.Legacy.Protocol;

namespace Enlist.Runner.Legacy.Execution;

/// <summary>
/// Ties discovery, the pipe protocol and plugin invocation together. One instance per runner
/// process, which is itself one instance per running application — see design doc section 2. Logic
/// identical to the modern Enlist.Runner's own RunnerHost — this is the net472 port, differing only in
/// namespace and in how its DiscoveryResult/IsolationInfo/MessageChannel dependencies are constructed
/// (see Program.cs, Hosting/LegacyPluginLoadContext.cs).
/// </summary>
public sealed class RunnerHost
{
    private readonly DiscoveryResult _discovery;
    private readonly MessageChannel<RunnerMessage, AgentCommand> _channel;

    // Both registries are written from DispatchAsync, which runs every command on its own thread-pool
    // task — so N StartServiceCommands sent back-to-back (which is how the agent starts an application)
    // are N concurrent writers. A plain Dictionary here corrupted itself under exactly that load and
    // the losing service was never started; on .NET Framework the same corruption can hang the process
    // in Dictionary.FindEntry instead of throwing.
    private readonly ConcurrentDictionary<string, RunningServiceState> _runningServices = new();

    /// <summary>In-flight job runs, keyed by RunId — see RunningJobState.</summary>
    private readonly ConcurrentDictionary<string, RunningJobState> _runningJobs = new();

    /// <summary>
    /// One task chain per service name, so Start and Stop for the SAME service are applied in the
    /// order they ARRIVED.
    ///
    /// A semaphore was here until 2026-09-12 and cannot deliver that. It grants mutual exclusion, but
    /// the two commands run on separate pool tasks that RACE to reach it, and about one run in four
    /// the Stop got there first: it found nothing in _runningServices, logged "not running -
    /// ignored", and sent no state message at all. The Start then reported Running, leaving a service
    /// that had been told to stop sitting in Running with no terminal message ever sent - and
    /// ConcurrentCommandTests blocking until its own timeout.
    ///
    /// The chain is extended on the read loop, which is the only place arrival order exists at all.
    /// Jobs are deliberately not chained: a CancelJobCommand queued behind the very run it is meant
    /// to cancel would never arrive in time to matter.
    /// </summary>
    private readonly Dictionary<string, Task> _serviceQueues = new(StringComparer.Ordinal);

    /// <summary>Guards _serviceQueues. Only the read loop extends a chain today; the lock is what keeps that from being a silent requirement on whoever edits Dispatch next.</summary>
    private readonly object _serviceQueueLock = new();

    // Log calls can arrive from arbitrary plugin threads/tasks (background work a service kicked off,
    // or Console.WriteLine from inside a job). Routing them through a Channel rather than calling
    // MessageChannel.SendAsync directly means a synchronous TextWriter.Write can enqueue without
    // blocking on pipe I/O, and a single pump task keeps writes to the pipe serialized.
    private readonly Channel<RunnerMessage> _outbound = Channel.CreateUnbounded<RunnerMessage>(
        new UnboundedChannelOptions { SingleReader = true });

    // Flows through await/Task.Run continuations (not raw Threads) so Console output emitted while a
    // specific service/job's method is executing gets attributed to it; falls back to "runner"
    // otherwise. Best-effort by nature — see LineBufferingTextWriter usage below.
    private readonly AsyncLocal<string?> _currentSource = new();

    private readonly IsolationInfo _isolation;

    public RunnerHost(DiscoveryResult discovery, IsolationInfo isolation, MessageChannel<RunnerMessage, AgentCommand> channel)
    {
        _discovery = discovery;
        _isolation = isolation;
        _channel = channel;

        // A line the channel could not read as a command is said to the agent, not lost with the channel.
        _channel.MessageSkipped += line => Log("runner", LogLevel.Warning, line);
    }

    /// <summary>
    /// Text writers for Console.Out/Error redirection. Installed by Program.cs immediately after
    /// construction and before any plugin assembly loads, so nothing a plugin does at module-init
    /// time can bypass capture (design doc section 7).
    /// </summary>
    public (TextWriter Out, TextWriter Error) CreateConsoleWriters() => (
        new LineBufferingTextWriter(line => Log(CurrentSourceOrRunner(), LogLevel.Information, line)),
        new LineBufferingTextWriter(line => Log(CurrentSourceOrRunner(), LogLevel.Error, line)));

    public async Task RunAsync(CancellationToken processExit)
    {
        var pump = PumpOutboundAsync(processExit);

        await _channel.SendAsync(new ReadyMessage(
            _discovery.Services.Select(s => new ServiceInfo(s.Name, s.Description, s.TypeName)).ToList(),
            _discovery.Jobs.Select(j => new JobInfo(j.Name, j.Description, j.TypeName, j.DeclaredCron)).ToList(),
            _discovery.Warnings,
            _isolation,
            _discovery.ApplicationDescription,
            RunnerProtocol.Version), processExit).ConfigureAwait(false);

        // The pipe closing without a Shutdown (the agent is gone) and a cancelled process token are
        // both treated exactly like an explicit Shutdown: stop everything, then exit — rather than
        // leaving services running with nobody left to tell the process to exit.
        var shutdownTimeout = TimeSpan.FromSeconds(10);

        while (!processExit.IsCancellationRequested)
        {
            AgentCommand? command;
            try
            {
                command = await _channel.ReceiveAsync(processExit).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // The connection failed rather than closed — a reset, which is what a dead agent looks
                // like from behind a container engine's port proxy. Same meaning as end-of-stream.
                break;
            }

            if (command is null)
            {
                break;
            }

            if (command is ShutdownCommand shutdown)
            {
                shutdownTimeout = TimeSpan.FromMilliseconds(shutdown.TimeoutMs);
                break;
            }

            // Dispatch DECIDES WHERE each command runs and returns immediately; it never runs one
            // here. That matters twice over. An async method runs synchronously until its first await
            // that actually yields, and a plugin method returning void never yields at all -
            // InvocationHelper calls method.Invoke and it runs to completion inline - so running a
            // command on this thread would execute the ENTIRE job or service body before the loop
            // called ReceiveAsync again: a long synchronous job made Cancel, Stop and even Shutdown
            // sit unread until it was over.
            //
            // And this loop is the only place that knows the order commands ARRIVED in, so it is the
            // only place that can preserve it. See _serviceQueues.
            //
            // Known limitation: a StartService still in flight when Shutdown arrives is not waited
            // for — a service that finishes starting after StopAllAsync took its snapshot exits with
            // the process rather than through its [EnlistStop].
            Dispatch(command);
        }

        // Guarded, because this is the one place an exception has nowhere to go: the read loop is
        // over, and anything escaping here leaves Main with an unhandled exception instead of a clean
        // exit — after which the agent reports a crash and retries an application that was actually
        // told to stop.
        try
        {
            await StopAllAsync(shutdownTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log("runner", LogLevel.Error, $"Error while stopping on shutdown: {ex}");
        }

        _outbound.Writer.TryComplete();
        await pump.ConfigureAwait(false);
    }

    /// <summary>
    /// Runs on the read loop and returns without executing anything: it only places each command on
    /// the right piece of machinery. Service commands join their service's chain, in arrival order;
    /// everything else goes straight to the pool.
    /// </summary>
    private void Dispatch(AgentCommand command)
    {
        switch (command)
        {
            case StartServiceCommand c:
                QueueForService(c.Service, command, () => HandleStartServiceAsync(c));
                break;

            case StopServiceCommand c:
                QueueForService(c.Service, command, () => HandleStopServiceAsync(c));
                break;

            case RunJobCommand c:
                _ = Task.Run(() => GuardedAsync(command, () => HandleRunJobAsync(c)));
                break;

            case CancelJobCommand c:
                _ = Task.Run(() => GuardedAsync(command, () =>
                {
                    HandleCancelJob(c);
                    return Task.CompletedTask;
                }));
                break;
        }
    }

    /// <summary>
    /// Appends to this service's chain so it runs after everything already queued for the same
    /// service, and so two commands read in one order are never applied in the other.
    /// </summary>
    private void QueueForService(string serviceName, AgentCommand command, Func<Task> action)
    {
        lock (_serviceQueueLock)
        {
            var previous = _serviceQueues.TryGetValue(serviceName, out var tail) ? tail : Task.CompletedTask;

            // TaskScheduler.Default and NOT ExecuteSynchronously: the continuation must never run on
            // the thread that completed the previous link, which on the first link is this one - the
            // read loop. Inlining it here would reintroduce exactly the blockage Dispatch exists to
            // avoid.
            var next = previous.ContinueWith(
                _ => GuardedAsync(command, action),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();

            // The chain is the tail only. A completed link is not kept, so this never grows beyond
            // one task per service name that has ever been commanded.
            _serviceQueues[serviceName] = next;
        }
    }

    /// <summary>
    /// The catch that used to live in DispatchAsync. Nothing above these calls can handle an
    /// exception - a service chain has no caller waiting on it, and a job runs on a bare Task.Run -
    /// so anything escaping here would be an unobserved task exception and silence.
    /// </summary>
    private async Task GuardedAsync(AgentCommand command, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log("runner", LogLevel.Error, $"Unhandled error dispatching {command.GetType().Name}: {ex}");
        }
    }

    private async Task HandleStartServiceAsync(StartServiceCommand command)
    {
        var service = _discovery.Services.FirstOrDefault(s => s.Name == command.Service);
        if (service is null)
        {
            Send(new FaultedMessage(command.Service, $"No discovered service named '{command.Service}'."));
            return;
        }

        if (_runningServices.ContainsKey(service.Name))
        {
            Log(service.Name, LogLevel.Warning, "Start requested but the service is already running - ignored.");
            return;
        }

        if (service.StartMethod is null)
        {
            Send(new FaultedMessage(service.Name, "Service has no [EnlistStart] method."));
            return;
        }

        _currentSource.Value = service.Name;
        Send(new StateChangedMessage(service.Name, RunnerTargetKind.Service, RunnerState.Starting));

        object instance;
        try
        {
            instance = CreateInstance(service.Type);
        }
        catch (Exception ex)
        {
            // StateChanged BEFORE the fault, exactly as the catch at the end of this method does.
            // AgentHost tracks service state from StateChangedMessage and only LOGS a FaultedMessage,
            // so a fault with no state change beside it left the service parked at Starting for the
            // life of the runner: a service that never even got constructed, reported as one that is
            // still starting, with the reason visible only to whoever read the log.
            Send(new StateChangedMessage(service.Name, RunnerTargetKind.Service, RunnerState.Faulted));
            Send(new FaultedMessage(service.Name, $"Could not construct {service.TypeName}: {Describe(ex)}"));
            return;
        }

        var cts = new CancellationTokenSource();
        var state = new RunningServiceState { Service = service, Instance = instance, StoppingCts = cts };
        _runningServices[service.Name] = state;

        try
        {
            // Bound inside the try: a Start declaring a parameter the runner cannot bind is this
            // service's fault, and must be reported as one — not as a runner-level dispatch error that
            // leaves the service parked at Starting.
            var args = ParameterBinder.Bind(service.StartMethod, cts.Token, LoadSettings(service.Type, service.Name), line => Log(service.Name, LogLevel.Information, line));

            await InvocationHelper.InvokeAsync(service.StartMethod, instance, args).ConfigureAwait(false);
            Send(new StateChangedMessage(service.Name, RunnerTargetKind.Service, RunnerState.Running));
        }
        catch (Exception ex)
        {
            _runningServices.TryRemove(service.Name, out _);
            cts.Dispose();
            Send(new StateChangedMessage(service.Name, RunnerTargetKind.Service, RunnerState.Faulted));
            Send(new FaultedMessage(service.Name, Describe(ex)));
        }
    }

    private async Task HandleStopServiceAsync(StopServiceCommand command)
    {
        if (!_runningServices.TryGetValue(command.Service, out var state))
        {
            Log(command.Service, LogLevel.Warning, "Stop requested but the service is not running - ignored.");
            return;
        }

        await StopOneAsync(state, TimeSpan.FromMilliseconds(command.TimeoutMs)).ConfigureAwait(false);
    }

    private async Task StopOneAsync(RunningServiceState state, TimeSpan timeout)
    {
        var service = state.Service;
        _currentSource.Value = service.Name;
        Send(new StateChangedMessage(service.Name, RunnerTargetKind.Service, RunnerState.Stopping));

        // Signal the token Start() was given, in case it's the only shutdown hook the plugin honors.
        state.StoppingCts.Cancel();

        var stopInvocation = service.StopMethod is null
            ? Task.CompletedTask
            : InvokeStopAsync(service, state.Instance);

        var completed = await Task.WhenAny(stopInvocation, Task.Delay(timeout)).ConfigureAwait(false);
        _runningServices.TryRemove(service.Name, out _);
        state.StoppingCts.Dispose();

        if (completed != stopInvocation)
        {
            // Advisory only — see StopServiceCommand.TimeoutMs. The agent, not the runner, is what
            // actually kills the process if this matters.
            Log(service.Name, LogLevel.Warning, $"[EnlistStop] did not return within {timeout.TotalSeconds:0}s.");
            Send(new StateChangedMessage(service.Name, RunnerTargetKind.Service, RunnerState.Stopping));

            // The invocation is still running and this method is the last thing holding it, so its
            // eventual outcome is reported from here or not at all. Dropped, it left the service at
            // Stopping permanently: a lone StopServiceCommand does not make the agent kill the
            // process, so nothing else was coming to correct the state, and a second Stop was
            // answered with "not running - ignored" because the registry entry is already gone.
            _ = stopInvocation.ContinueWith(
                finished =>
                {
                    if (finished.IsFaulted)
                    {
                        Send(new FaultedMessage(service.Name, Describe(finished.Exception!)));
                    }

                    Send(new StateChangedMessage(service.Name, RunnerTargetKind.Service, RunnerState.Stopped));
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);

            return;
        }

        if (stopInvocation.IsFaulted)
        {
            Send(new FaultedMessage(service.Name, Describe(stopInvocation.Exception!)));
        }

        Send(new StateChangedMessage(service.Name, RunnerTargetKind.Service, RunnerState.Stopped));
    }

    /// <summary>
    /// Async rather than expression-bodied so that a binding failure on the Stop method surfaces as a
    /// faulted task — which StopOneAsync already turns into a FaultedMessage — instead of a synchronous
    /// throw that would escape StopAllAsync's Task.WhenAll.
    /// </summary>
    private async Task InvokeStopAsync(DiscoveredService service, object instance)
    {
        var args = ParameterBinder.Bind(
            service.StopMethod!, CancellationToken.None, LoadSettings(service.Type, service.Name),
            line => Log(service.Name, LogLevel.Information, line));

        await InvocationHelper.InvokeAsync(service.StopMethod!, instance, args).ConfigureAwait(false);
    }

    /// <summary>
    /// Everything this runner is doing, wound down together: services get their [EnlistStop], in-flight
    /// job runs get their token cancelled and the same grace period to unwind. Jobs used to be left out
    /// of this entirely, so a shutdown mid-run exited the process out from under work in progress.
    /// </summary>
    private async Task StopAllAsync(TimeSpan timeout)
    {
        var stops = _runningServices.Values.ToList().Select(s => StopOneAsync(s, timeout)).ToList();

        var runs = _runningJobs.Values.ToList();
        foreach (var run in runs)
        {
            Log(run.JobName, LogLevel.Information, $"Shutting down - cancelling run '{run.RunId}'.");
            run.Cancel();
        }

        await Task.WhenAll(stops).ConfigureAwait(false);

        if (runs.Count == 0)
        {
            return;
        }

        // A job that never observes its token cannot be stopped from in here, so this waits rather
        // than blocks: overrunning the grace period is reported and the process goes on exiting.
        // Killing a runner that refuses to leave is the agent's call, not this loop's.
        var draining = Task.WhenAll(runs.Select(r => r.Completion));
        if (await Task.WhenAny(draining, Task.Delay(timeout)).ConfigureAwait(false) != draining)
        {
            var stuck = runs.Where(r => !r.Completion.IsCompleted).Select(r => $"{r.JobName}/{r.RunId}");
            Log("runner", LogLevel.Warning,
                $"Job run(s) still going after the {timeout.TotalSeconds:0.#}s grace period: {string.Join(", ", stuck)}.");
        }
    }

    private async Task HandleRunJobAsync(RunJobCommand command)
    {
        var job = _discovery.Jobs.FirstOrDefault(j => j.Name == command.Job);
        if (job is null)
        {
            Send(new FaultedMessage(command.Job, $"No discovered job named '{command.Job}'."));
            return;
        }

        if (job.ExecuteMethod is null)
        {
            Send(new JobResultMessage(job.Name, command.RunId, JobOutcome.Failed, "Job has no [EnlistExecute] method.", 0));
            return;
        }

        _currentSource.Value = job.Name;
        Send(new StateChangedMessage(job.Name, RunnerTargetKind.Job, RunnerState.Starting));

        var settings = MergeSettings(LoadSettings(job.Type, job.Name), command.Settings);
        var sw = Stopwatch.StartNew();

        // Registered before the invocation and removed in the finally, so a Cancel arriving while this
        // runs has a token to trip and one arriving after it does not.
        var run = new RunningJobState(job.Name, command.RunId);
        _runningJobs[command.RunId] = run;

        try
        {
            var instance = CreateInstance(job.Type);
            var args = ParameterBinder.Bind(job.ExecuteMethod, run.Token, settings, line => Log(job.Name, LogLevel.Information, line));

            Send(new StateChangedMessage(job.Name, RunnerTargetKind.Job, RunnerState.Running));
            await InvocationHelper.InvokeAsync(job.ExecuteMethod, instance, args).ConfigureAwait(false);

            // Checked rather than assumed. A job that honors cancellation by returning normally is
            // just as correct as one that throws, and calling that a clean success would tell the
            // operator their stop did nothing.
            if (run.IsCancellationRequested)
            {
                ReportCancelled(job.Name, command.RunId, sw);
            }
            else
            {
                Send(new StateChangedMessage(job.Name, RunnerTargetKind.Job, RunnerState.Stopped));
                Send(new JobResultMessage(job.Name, command.RunId, JobOutcome.Succeeded, Error: null, sw.ElapsedMilliseconds));
            }
        }
        catch (Exception ex) when (run.IsCancellationRequested && IsCancellation(ex))
        {
            ReportCancelled(job.Name, command.RunId, sw);
        }
        catch (Exception ex)
        {
            Send(new StateChangedMessage(job.Name, RunnerTargetKind.Job, RunnerState.Faulted));
            Send(new JobResultMessage(job.Name, command.RunId, JobOutcome.Failed, Describe(ex), sw.ElapsedMilliseconds));
        }
        finally
        {
            _runningJobs.TryRemove(command.RunId, out _);
            run.Complete();
        }
    }

    /// <summary>
    /// A cancelled run is neither a fault nor a success: it was stopped deliberately and did not
    /// finish its work. Stopped rather than Faulted keeps it out of the "something broke" bucket;
    /// the unsuccessful JobResult keeps it out of "ran to completion".
    /// </summary>
    private void ReportCancelled(string jobName, string runId, Stopwatch sw)
    {
        Send(new StateChangedMessage(jobName, RunnerTargetKind.Job, RunnerState.Stopped));
        Send(new JobResultMessage(jobName, runId, JobOutcome.Cancelled, "Run was cancelled before it finished.", sw.ElapsedMilliseconds));
    }

    /// <summary>
    /// Unwrapped, because a synchronous plugin method reaches us through MethodInfo.Invoke and its
    /// OperationCanceledException arrives inside a TargetInvocationException — matching on the outer
    /// type alone would report every cancelled synchronous job as faulted.
    /// </summary>
    private static bool IsCancellation(Exception ex) => ex switch
    {
        OperationCanceledException => true,
        TargetInvocationException { InnerException: { } inner } => IsCancellation(inner),
        AggregateException { InnerException: { } inner } => IsCancellation(inner),
        _ => false,
    };

    /// <summary>
    /// Cancels every in-flight run of one job — all of them rather than one named run, because the
    /// operator's intent is "stop this job" and the agent has no run id to name: it fires a run and
    /// forgets it.
    ///
    /// The runner cannot stop the work itself. It has no safe way to halt another thread, so all it
    /// can do is trip the token the job was handed; whether that has any effect is the plugin's, not
    /// the runner's. Hence the warning naming the run rather than a claim that it stopped.
    ///
    /// Separate from the agent's scheduler-level stop, which unregisters the job so it stops FIRING
    /// and never reaches a runner at all. Stopping a job in the portal does both.
    /// </summary>
    private void HandleCancelJob(CancelJobCommand command)
    {
        if (_discovery.Jobs.All(j => j.Name != command.Job))
        {
            Send(new FaultedMessage(command.Job, $"No discovered job named '{command.Job}'."));
            return;
        }

        var runs = _runningJobs.Values.Where(r => r.JobName == command.Job).ToList();
        if (runs.Count == 0)
        {
            // Not an error, and the ordinary way to get here: stopping a job whose last run already
            // finished. Saying so beats silence, which reads as a command that went nowhere.
            Log(command.Job, LogLevel.Information, "Cancel requested, but no run of this job is in flight - nothing to cancel.");
            return;
        }

        foreach (var run in runs)
        {
            run.Cancel();
            Log(command.Job, LogLevel.Warning,
                $"Cancellation requested for run '{run.RunId}'. It stops when the job next observes its " +
                "CancellationToken - a job that never checks it runs to completion regardless.");
        }
    }

    private static object CreateInstance(Type type)
    {
        var ctor = type.GetConstructor(Type.EmptyTypes)
            ?? throw new InvalidOperationException($"{type.FullName} has no public parameterless constructor. Constructor injection is not part of the Enlist contract - see docs/03-architecture/enList-v3-Design.md section 3.");
        return ctor.Invoke(null);
    }

    /// <summary>
    /// overrides is declared non-nullable on RunJobCommand and can still arrive null: a command whose
    /// JSON simply omits "settings" deserializes to exactly that. Unguarded, this threw an
    /// NullReferenceException AFTER StateChanged(Starting) had gone out and BEFORE the try that turns
    /// a job failure into a JobResultMessage - so the run was announced, never reported, and the
    /// agent's in-flight entry for it was never cleared.
    /// </summary>
    private static Dictionary<string, string> MergeSettings(IReadOnlyDictionary<string, string> baseSettings, IReadOnlyDictionary<string, string>? overrides)
    {
        // net472's Dictionary<TKey,TValue> has no constructor accepting IReadOnlyDictionary (only
        // IDictionary) — the netcoreapp-only overload the modern runner relies on here.
        var merged = baseSettings.ToDictionary(kv => kv.Key, kv => kv.Value);
        if (overrides is null)
        {
            return merged;
        }

        foreach (var (key, value) in overrides)
        {
            merged[key] = value;
        }

        return merged;
    }

    private static string Describe(Exception ex) => ex is AggregateException agg && agg.InnerException is not null
        ? agg.InnerException.ToString()
        : ex.ToString();

    private string CurrentSourceOrRunner() => _currentSource.Value ?? "runner";

    /// <summary>Settings for one plugin type, with a malformed file said in that plugin's own log rather than swallowed as "no settings".</summary>
    private IReadOnlyDictionary<string, string> LoadSettings(Type type, string source) =>
        SettingsLoader.Load(type, warning => Log(source, LogLevel.Warning, warning));

    private void Log(string source, LogLevel level, string text) =>
        _outbound.Writer.TryWrite(new LogMessage(source, level, text, DateTimeOffset.UtcNow));

    private void Send(RunnerMessage message) => _outbound.Writer.TryWrite(message);

    private async Task PumpOutboundAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var message in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await _channel.SendAsync(message, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Process is exiting — best-effort delivery only, nothing left to hand undelivered messages to.
        }
        catch (IOException)
        {
            // The pipe went away (agent process died, or killed us mid-shutdown). Nothing more to send to.
        }
        catch (Exception ex)
        {
            // Anything else - an ObjectDisposedException from a stream torn down underneath this is
            // the realistic one. Left to fault the task, it was rethrown by `await pump` at the end of
            // RunAsync and escaped Main: a non-zero exit that the agent reads as a crash and retries,
            // for a runner that was shutting down cleanly. And every Log and Send after that point
            // queued into a channel with no reader, so the diagnosis went into the queue too.
            //
            // Written to the REAL stderr, not Console.Error: Program.cs redirects that back into this
            // very channel, so reporting the pump's death through it would be the one message
            // guaranteed never to arrive.
            try
            {
                using var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
                stderr.WriteLine($"enlist-runner: the outbound message pump stopped and nothing more will be sent - {ex}");
            }
            catch
            {
                // There is no third place to report this to.
            }
        }
    }
}
