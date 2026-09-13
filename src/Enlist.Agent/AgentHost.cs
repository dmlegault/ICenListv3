using System.Collections.Concurrent;

using Enlist.Agent.Configuration;
using Enlist.Agent.Hosting;
using Enlist.Agent.Logging;
using Enlist.Agent.Scheduling;
using Enlist.Agent.Status;
using Enlist.Agent.Supervision;
using Enlist.ControlPlane.Contracts;
using Enlist.Runner.Protocol;

namespace Enlist.Agent;

/// <summary>
/// Supervises runners, owns the scheduler, collects logs. Where assignments come from is abstracted
/// behind IAssignmentSource — a fixed local list (via the AgentAssignments constructor overload) or
/// the control plane (ControlPlaneAssignmentSource). Either way it reconciles ONGOING changes, not
/// just a one-time startup list: IAssignmentSource.WaitForChangeAsync drives a
/// background loop that starts newly-assigned applications, stops removed/deassigned ones, and
/// restarts ones whose Path or CronOverrides changed — all without restarting the agent process
/// itself, which is the entire point (today: log into each of N machines individually and copy files
/// by hand; this replaces that with "push a change to the control plane, every affected agent
/// reconciles on its own").
///
/// Also hardened with: auto-retry (backoff + give-up cap) of an application that fails to start,
/// fails to report in, or crashes after starting — one shared decision in
/// ScheduleRetryOrGiveUpAsync, so a CPU-starved box (tiworker.exe pegging every core right after a
/// patch reboot) gets retried until the storm passes — a Job Object so an abnormal agent death takes
/// its runners with it (see Hosting/JobObject.cs), log retention, and a Logs/status.json snapshot
/// (optionally also pushed to the control plane via IStatusReporter) of what's actually running.
/// </summary>
public sealed class AgentHost : IAsyncDisposable
{
    private readonly IAssignmentSource _assignmentSource;
    private readonly IStatusReporter _statusReporter;

    /// <summary>Keyed by RuntimeFlavor (see Enlist.ControlPlane.Contracts.RuntimeFlavors) — which enlist-runner build to stage for a given application. Always contains at least RuntimeFlavors.Default; additional entries (e.g. a legacy net472 runner) are supplied via the constructor's additionalRunnerBinDirectories parameter, letting one agent host both a modern and a legacy runtime side by side.</summary>
    private readonly IReadOnlyDictionary<string, string> _runnerBinDirectories;
    private readonly string _stagingRoot;
    private readonly AgentFileLogSink _logSink;

    /// <summary>Kept only so StopAsync can dispose it, which is what gives its buffered lines a final flush. The sink is what actually uses it.</summary>
    private readonly ILogForwarder? _logForwarder;

    /// <summary>How long shutdown will wait for the control plane to accept the agent's last word. Long enough for a healthy control plane on a busy host, short enough that an unreachable one cannot hold up a service stop.</summary>
    private static readonly TimeSpan FinalSnapshotTimeout = TimeSpan.FromSeconds(5);

    private readonly AgentStatusWriter _statusWriter;
    private readonly AgentHostOptions _options;
    private readonly CrashBackoff _backoff;
    // Concurrency model, stated once. This host is entered from several independent contexts at the
    // same time — the reconciliation loop, crash-retries on the thread pool, Process.Exited, the SignalR
    // command handler, and one receive loop per running application (HandleMessageAsync) — and the
    // status snapshot enumerates everything below from the heartbeat loop and from every one of those
    // receive loops. Three rules keep that safe:
    //
    //   1. Every shared registry is a concurrent collection, so enumeration never throws and a write
    //      from one context never corrupts another's. Per-field writes on a tracked state are benign.
    //   2. The LIFECYCLE of one application — start, stop, restart, crash cleanup — is serialized
    //      through that application's gate (WithLifecycleGateAsync). Per application, deliberately: a
    //      thirty-second start of one application must not stall every other's message handling,
    //      which one host-wide lock would do.
    //   3. Snapshots are generated and reported under one lock, so an older snapshot cannot overtake a
    //      newer one on the way to the control plane.
    private readonly ConcurrentDictionary<string, IRunnerInstance> _instances = new();
    private readonly ConcurrentDictionary<string, AppTrackedState> _appState = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _lifecycleGates = new();
    private readonly SemaphoreSlim _snapshotLock = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();

    /// <summary>0 until StopAsync has run - see StopAsync, which DisposeAsync calls straight into.</summary>
    private int _stopped;
    private readonly List<Task> _pendingRetries = new();
    private readonly object _pendingRetriesLock = new();
    private AgentAssignments? _currentAssignments;
    private JobScheduler? _scheduler;
    private Task? _retentionSweepLoop;
    private Task? _reconciliationLoop;
    private Task? _heartbeatLoop;

    /// <summary>Windows-only, and optional even there — see JobObject.cs. Owned here because its lifetime is the agent's, but used only by the process backend it's handed to.</summary>
    private readonly JobObject? _jobObject;

    /// <summary>
    /// How runners get created, keyed by IsolationModes — deliberately the same shape as
    /// _runnerBinDirectories above, because it is the same kind of lookup: an application declares what
    /// it needs, and an agent either has a way to provide it or cannot start it.
    ///
    /// Always contains IsolationModes.Process. A container backend is present only when this agent was
    /// configured with one, which is what makes "this agent cannot run containers" a clear, reportable
    /// state rather than a crash.
    ///
    /// AgentHost must never branch on WHICH backend it got — see docs/03-architecture/Container-Story.md §6.4. Selecting
    /// one by declared mode is not branching on backend type; nothing downstream of the lookup knows or
    /// cares what came back.
    /// </summary>
    private readonly IReadOnlyDictionary<string, IRunnerBackend> _runnerBackends;

    /// <summary>Test/observability hook — the real decision logic lives in HandleMessageAsync, this is purely "and also tell whoever's watching."</summary>
    public event Action<IRunnerInstance, RunnerMessage>? MessageReceived;

    public IReadOnlyDictionary<string, IRunnerInstance> Instances => _instances;

    private readonly string? _containerImage;

    // The engine the container backend drives, so the capability probe asks the same one — not a second instance per heartbeat.
    private readonly IContainerEngine? _containerEngine;

    /// <summary>
    /// What this agent can actually do, probed rather than assumed.
    ///
    /// The isolation-mode list is configuration — it says what was asked for. The container engine
    /// status is the part that required going and looking: being started with an image name does not
    /// mean a container engine exists, is running, or can be reached, and an agent in that state would
    /// otherwise advertise a capability it does not have and then fail every container policy.
    /// </summary>
    public async Task<AgentCapabilitiesDto> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        ContainerEngineStatus? engine = null;

        if (_containerImage is not null)
        {
            var probe = await (_containerEngine ?? new DockerContainerEngine()).ProbeAsync(ct).ConfigureAwait(false);
            engine = new ContainerEngineStatus(probe.Available, probe.Version, probe.Error, _containerImage, DateTimeOffset.UtcNow, Engine: (_containerEngine ?? new DockerContainerEngine()).Name);
        }

        return new AgentCapabilitiesDto(_runnerBackends.Keys.ToList(), engine, (_assignmentSource as IReportsPushChannel)?.PushChannelState);
    }

    public string StatusFilePath => _statusWriter.FilePath;

    /// <summary>A fixed list that never changes for the life of the agent — wrapped in StaticAssignmentSource, which reads once at startup and never signals a change.</summary>
    public AgentHost(
        AgentAssignments assignments,
        string runnerBinDirectory,
        string stagingRoot,
        string logRoot,
        AgentHostOptions? options = null,
        IReadOnlyDictionary<string, string>? additionalRunnerBinDirectories = null,
        string? containerImage = null,
        string? agentName = null,
        IContainerEngine? containerEngine = null)
        : this(new StaticAssignmentSource(assignments), runnerBinDirectory, stagingRoot, logRoot, options, null, null, additionalRunnerBinDirectories, null, containerImage, agentName, containerEngine)
    {
    }

    /// <summary>
    /// Assignments (and their changes) come from wherever assignmentSource points, e.g. ControlPlaneAssignmentSource. statusReporter is where the snapshot goes beyond the local status.json; null means nowhere. logForwarder likewise mirrors app log lines beyond the local files — null means nowhere.
    /// runnerBinDirectory is always registered under RuntimeFlavors.Default; additionalRunnerBinDirectories supplies any other flavor this agent can also host (e.g. RuntimeFlavors.NetFramework472 for a legacy runner build) — see docs/03-architecture/SAD.md §9. An assignment naming a flavor with no entry here simply can't be started by this agent (see StartApplicationAsync).
    /// </summary>
    public AgentHost(
        IAssignmentSource assignmentSource,
        string runnerBinDirectory,
        string stagingRoot,
        string logRoot,
        AgentHostOptions? options = null,
        IStatusReporter? statusReporter = null,
        ILogForwarder? logForwarder = null,
        IReadOnlyDictionary<string, string>? additionalRunnerBinDirectories = null,
        IReadOnlyDictionary<string, IRunnerBackend>? runnerBackends = null,
        string? containerImage = null,
        string? agentName = null,

        // Which engine drives containers when containerImage is set — Docker unless told otherwise. Chosen
        // by Program.cs from --container-engine; nothing here ever asks which one it got.
        IContainerEngine? containerEngine = null)
    {
        _assignmentSource = assignmentSource;
        _statusReporter = statusReporter ?? NullStatusReporter.Instance;

        var runnerBinDirectories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [RuntimeFlavors.Default] = runnerBinDirectory,
        };
        if (additionalRunnerBinDirectories is not null)
        {
            foreach (var (flavor, path) in additionalRunnerBinDirectories)
            {
                runnerBinDirectories[flavor] = path;
            }
        }

        _runnerBinDirectories = runnerBinDirectories;
        _stagingRoot = stagingRoot;
        _logForwarder = logForwarder;
        _logSink = new AgentFileLogSink(logRoot, logForwarder);
        _statusWriter = new AgentStatusWriter(logRoot);
        _options = options ?? AgentHostOptions.Default;
        _backoff = new CrashBackoff(_options.MaxRestartAttempts, _options.RestartBaseDelay, _options.RestartMaxDelay, _options.RestartSettledDuration);

        if (OperatingSystem.IsWindows())
        {
            _jobObject = new JobObject();
        }

        // The process backend always exists — an agent can always run a child process. Anything else is
        // opt-in: an agent with no container engine configured simply has no container entry, and a
        // policy asking for one is reported as a configuration problem rather than crashing.
        var backends = new Dictionary<string, IRunnerBackend>(StringComparer.OrdinalIgnoreCase)
        {
            [IsolationModes.Process] = new ProcessRunnerBackend(_jobObject, _options.ConnectTimeout, _logSink, stagingRoot),
        };

        // Built here rather than handed in so the container backend shares AgentHost's own log sink.
        // Two AgentFileLogSink instances would append to the same daily file from two places, which is
        // a contention bug waiting to happen for no benefit.
        _containerImage = containerImage;

        if (containerImage is not null)
        {
            backends[IsolationModes.Container] = new ContainerRunnerBackend(
                _containerEngine = containerEngine ?? new DockerContainerEngine(), containerImage, _options.ConnectTimeout, _logSink,

                // Defaults to the machine name, matching what --agent itself defaults to, so the
                // ownership label is stable across restarts even when no name was passed explicitly.
                agentName ?? Environment.MachineName);
        }

        // Last word, and the seam tests use it — an explicitly supplied backend beats anything built above.
        if (runnerBackends is not null)
        {
            foreach (var (mode, backend) in runnerBackends)
            {
                backends[mode] = backend;
            }
        }

        _runnerBackends = backends;

        // Best-effort components say when they fail (rate-limited) and when they recover — into the
        // agent log, the one place an operator reads. A control plane returning 500 to every status
        // POST for an hour used to be indistinguishable from one that was fine.
        foreach (var component in new object?[] { assignmentSource, statusReporter, logForwarder })
        {
            if (component is IReportsDiagnostics reports)
            {
                reports.Diagnostic += message => _ = _logSink.WriteAgentLogAsync(message);
            }
        }

        // A push-channel state change re-reports capabilities at once rather than at the next
        // heartbeat, so the portal's "not receiving" chip reads current.
        if (assignmentSource is IReportsPushChannel pushChannel)
        {
            pushChannel.PushChannelStateChanged += () => _ = ReportCapabilitiesAsync(_shutdownCts.Token);
        }
    }

    public async Task StartAsync()
    {
        _scheduler = new JobScheduler(RunJobDueAsync, _logSink.WriteAgentLogAsync);
        _retentionSweepLoop = RunRetentionSweepLoopAsync(_shutdownCts.Token);
        _heartbeatLoop = RunHeartbeatLoopAsync(_shutdownCts.Token);

        // BEFORE the first reconcile, while this agent demonstrably owns nothing: anything a backend
        // still considers ours is therefore a leftover from a previous, unclean exit. Asking every
        // backend rather than the container one specifically keeps AgentHost free of backend knowledge
        // (docs/03-architecture/Container-Story.md §6.4) — a backend with nothing to reap does nothing.
        foreach (var backend in _runnerBackends.Values)
        {
            try
            {
                await backend.ReapOrphansAsync(_shutdownCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _logSink.WriteAgentLogAsync($"Orphan cleanup failed: {ex}").ConfigureAwait(false);
            }
        }

        // Guarded exactly as the reconciliation loop guards the same two calls, and for a sharper
        // reason. Unguarded, one unresolvable package digest at boot - a 404, a hash that does not
        // match - threw out of StartAsync, the hosted service failed to start, and the SCM restart-
        // looped the whole agent: an agent that starts NOTHING, forever, because one application
        // could not be resolved. Mid-life the identical failure costs one reconcile pass.
        //
        // The control-plane-is-down signal is deliberately left where it belongs: ConnectAsync in
        // Program.cs, before this host is ever constructed.
        try
        {
            var initial = await _assignmentSource.GetCurrentAsync(_shutdownCts.Token).ConfigureAwait(false);
            await ReconcileAsync(initial).ConfigureAwait(false);
            await PruneAssignmentSourcePackageCacheAsync(_shutdownCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _logSink.WriteAgentLogAsync(
                $"Initial reconciliation failed - starting anyway, and retrying on the next change or refetch: {ex}").ConfigureAwait(false);
        }

        _reconciliationLoop = RunReconciliationLoopAsync(_shutdownCts.Token);

        // Once up front too, so the portal is correct immediately rather than after a heartbeat interval.
        await ReportCapabilitiesAsync(_shutdownCts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Called after every ReconcileAsync, never before — see IPrunesPackageCache and
    /// ControlPlaneAssignmentSource's own note on the file-lock race pruning can lose:
    /// only once reconciliation has actually stopped whatever was using a superseded digest's files
    /// (StopApplicationAsync awaits the OS process being gone, not just asked to stop) is deleting
    /// them guaranteed to succeed rather than silently no-op on a locked file.
    /// </summary>
    private async Task PruneAssignmentSourcePackageCacheAsync(CancellationToken ct)
    {
        if (_assignmentSource is not IPrunesPackageCache pruner)
        {
            return;
        }

        try
        {
            await pruner.PruneUnusedPackagesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _logSink.WriteAgentLogAsync($"Package cache pruning failed: {ex}").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Diffs newAssignments against what was previously known (null the first time, meaning "everything
    /// is new") and converges: stop what's no longer wanted, start what's newly wanted, restart what
    /// changed. Called once from StartAsync for the initial list, then again every time
    /// IAssignmentSource.WaitForChangeAsync completes.
    /// </summary>
    private async Task ReconcileAsync(AgentAssignments newAssignments)
    {
        var previousByName = _currentAssignments?.Applications.ToDictionary(a => a.Name) ?? new Dictionary<string, ApplicationAssignment>();
        var newByName = newAssignments.Applications.ToDictionary(a => a.Name);
        _currentAssignments = newAssignments;

        foreach (var name in previousByName.Keys)
        {
            var stillWantedRunning = newByName.TryGetValue(name, out var stillAssigned) && stillAssigned.DesiredState == DesiredState.Running;
            if (!stillWantedRunning)
            {
                // A no-op when nothing is running, so no ContainsKey check outside the gate — which
                // would be a decision made on a registry another context may change before it is acted on.
                await WithLifecycleGateAsync(name, () => StopApplicationAsync(name)).ConfigureAwait(false);
            }

            if (!newByName.ContainsKey(name))
            {
                // Fully removed from desired state (not just turned Stopped) — nothing left to track.
                _appState.TryRemove(name, out _);
            }
        }

        foreach (var (name, app) in newByName)
        {
            var state = _appState.GetOrAdd(name, _ => new AppTrackedState());

            // Does this pass carry anything NEW about this application? It usually does not. The
            // source has a three-minute refetch floor, so WaitForChangeAsync completes on a timer
            // whether or not anything changed, and this method then runs over an identical list.
            //
            // Treating every pass as news meant a Stopped application re-logged "not starting" and a
            // Failed one re-logged its failure and pushed a fresh status snapshot, every three
            // minutes, forever - and worse, see StartApplicationAsync's give-up check.
            var isNew = !previousByName.TryGetValue(name, out var previous);
            var changed = !isNew && !SameAssignment(previous!, app);
            var news = isNew || changed;

            if (app.DesiredState != DesiredState.Running)
            {
                state.State = ApplicationState.Stopped;
                if (news)
                {
                    await _logSink.WriteAgentLogAsync($"{app.Name}: desired state is {app.DesiredState} - not starting.").ConfigureAwait(false);
                }

                continue;
            }

            // Stop-then-start for a changed assignment happens under ONE hold of the gate, so nothing
            // else — a crash-retry, most likely — can slip in between and start the old assignment.
            await WithLifecycleGateAsync(name, async () =>
            {
                if (changed && _instances.ContainsKey(name))
                {
                    await _logSink.WriteAgentLogAsync($"{app.Name}: assignment changed - restarting to pick it up.").ConfigureAwait(false);
                    await StopApplicationAsync(name).ConfigureAwait(false);
                }

                // A no-op when the application is already running — the common case on every pass.
                await StartApplicationAsync(app, news).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        await WriteStatusSnapshotAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Serializes the lifecycle operations of ONE application - start, stop, restart, crash cleanup -
    /// against each other, without serializing applications against one another. Callers of
    /// StartApplicationAsync, StopApplicationAsync and the crash-exit cleanup hold this; nothing else
    /// needs it (see the concurrency model on the registries above).
    ///
    /// An application's gate is never removed, not even when the application is unassigned, and that
    /// is deliberate. Removing it is the racy operation: a caller that has already taken the gate can
    /// still be inside its action when the entry is dropped, and the next caller would then GetOrAdd a
    /// FRESH semaphore and run that application's lifecycle concurrently with the first - losing
    /// exactly the guarantee this method exists to provide. Doing it safely means reference counting,
    /// which is a lot of machinery to reclaim a SemaphoreSlim. The dictionary is bounded by the number
    /// of DISTINCT application names ever assigned to this agent (names recur across redeployments;
    /// they are not per-deployment), so it settles rather than grows with time. Nothing disposes these
    /// either: SemaphoreSlim only holds an unmanaged handle once AvailableWaitHandle is read, and
    /// nothing here reads it.
    /// </summary>
    private async Task WithLifecycleGateAsync(string applicationName, Func<Task> action)
    {
        var gate = _lifecycleGates.GetOrAdd(applicationName, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RunReconciliationLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _assignmentSource.WaitForChangeAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var latest = await _assignmentSource.GetCurrentAsync(ct).ConfigureAwait(false);
                await ReconcileAsync(latest).ConfigureAwait(false);
                await PruneAssignmentSourcePackageCacheAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                await _logSink.WriteAgentLogAsync($"Reconciliation failed: {ex}").ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A configuration-level failure with no transient component: a policy conflict, a runtime flavor
    /// this agent cannot host, an isolation mode it has no backend for. Retrying cannot help, so the
    /// application goes straight to Failed without touching the crash-backoff machinery.
    ///
    /// Said out loud only when it is news — the first time the application enters Failed, or when the
    /// assignment changed. An unchanged refetch pass is silent: none of these conditions can resolve
    /// on their own, the state is already Failed, and the heartbeat goes on reporting it. Repeating
    /// the line every three minutes only buries the pass where it first happened.
    /// </summary>
    private async Task FailWithoutRetryAsync(AppTrackedState state, string applicationName, string reason, bool announce)
    {
        var alreadyFailed = state.State == ApplicationState.Failed;
        state.State = ApplicationState.Failed;

        if (!announce && alreadyFailed)
        {
            return;
        }

        await _logSink.WriteAgentLogAsync($"{applicationName}: {reason}").ConfigureAwait(false);
        await WriteStatusSnapshotAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Whether two assignments for one application say the same thing — i.e. whether a reconcile pass
    /// is carrying news. Every field a running instance cannot adopt without being restarted is here,
    /// which is why this doubles as the restart test.
    ///
    /// Isolation belongs in it for the same reason Path does: it decides HOW the application is
    /// hosted. Without it, flipping a rule from process to container did nothing until some unrelated
    /// event forced a restart — silently defeating the migrate-one-agent-at-a-time workflow that is
    /// the whole point of isolation being a policy field.
    ///
    /// AgreesWith, never ==: the spec is a positional record whose Ports/Networks/Env compare by
    /// REFERENCE under the compiler-generated equality, so == would report every deserialized-fresh
    /// spec as different and restart the application on every single pass.
    /// </summary>
    private static bool SameAssignment(ApplicationAssignment previous, ApplicationAssignment current) =>
        previous.DesiredState == current.DesiredState &&
        previous.Path == current.Path &&
        previous.RuntimeFlavor == current.RuntimeFlavor &&
        previous.ConflictReason == current.ConflictReason &&
        CronOverridesEqual(previous.CronOverrides, current.CronOverrides) &&
        previous.Isolation.AgreesWith(current.Isolation);

    private static bool CronOverridesEqual(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var otherValue) || otherValue != value)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Callers hold the application's lifecycle gate — see WithLifecycleGateAsync.
    ///
    /// freshIntent says whether this call carries new intent — a new or changed assignment, or an
    /// explicit retry once a backoff has elapsed — as opposed to the three-minute refetch coming
    /// round again over an identical list. The distinction decides whether a given-up application is
    /// touched at all, and whether a terminal failure is worth saying out loud again.
    /// </summary>
    private async Task StartApplicationAsync(ApplicationAssignment app, bool freshIntent)
    {
        if (_instances.ContainsKey(app.Name))
        {
            // Already running — an unchanged application on an ordinary reconcile pass, or a
            // crash-retry arriving after a reconcile restarted this application itself.
            return;
        }

        if (!_appState.TryGetValue(app.Name, out var state))
        {
            // Unassigned since this was scheduled — e.g. a pending crash-retry for an app that's since
            // been removed from desired state entirely via reconciliation. Nothing to start.
            return;
        }

        var gaveUp = state.State == ApplicationState.Failed && state.ConsecutiveFailures > _backoff.MaxAttempts;

        if (gaveUp && !freshIntent)
        {
            // It gave up, and nothing has changed since. "Not retrying again automatically" has to
            // mean that. Without this check the next refetch pass started the entire backoff cycle
            // over - and the one after that, every three minutes for as long as the agent ran - so an
            // application the agent had declared dead flapped Failed to Starting to Failed forever,
            // saying so in the log each time. Changing the assignment is what un-gives-up.
            return;
        }

        if (gaveUp)
        {
            // New intent after a give-up starts the count again, so the backoff measures THIS
            // assignment's failures instead of resuming a streak that belonged to the old one.
            state.ConsecutiveFailures = 0;
        }

        if (app.ConflictReason is not null)
        {
            // Also a configuration problem, not a transient one, and also discovered before any of the
            // other checks below get a chance to run — there's no single winning Path/PackageDigest to
            // even evaluate a runtime flavor against, since the control plane found two or more
            // disagreeing policy rules and refused to guess which one should win (see
            // ApplicationPolicyDto.ConflictReason).
            await FailWithoutRetryAsync(state, app.Name, app.ConflictReason, freshIntent).ConfigureAwait(false);
            return;
        }

        if (!_runnerBinDirectories.TryGetValue(app.RuntimeFlavor, out var runnerBinDirectory))
        {
            // A configuration problem, not a transient one — retrying won't make a runner-bin appear,
            // so this goes straight to Failed rather than through the crash-backoff/retry machinery.
            await FailWithoutRetryAsync(state, app.Name,
                $"requires runtime flavor '{app.RuntimeFlavor}', but this agent has no runner-bin configured for it - not starting. " +
                $"Configured flavors: [{string.Join(", ", _runnerBinDirectories.Keys)}].", freshIntent).ConfigureAwait(false);
            return;
        }

        if (!_runnerBackends.TryGetValue(app.Isolation.Mode, out var backend))
        {
            // Exactly the same posture as the missing runner-bin above, for exactly the same reason: an
            // agent with no container engine configured will not grow one by being retried. The most
            // likely cause by far is a policy asking for container isolation on an agent that was never
            // set up for it, so the message names the mode and what this agent actually supports.
            await FailWithoutRetryAsync(state, app.Name,
                $"requires '{app.Isolation.Mode}' isolation, but this agent has no backend configured for it - not starting. " +
                $"Configured isolation modes: [{string.Join(", ", _runnerBackends.Keys)}].", freshIntent).ConfigureAwait(false);
            return;
        }

        state.State = ApplicationState.Starting;
        state.ServiceStates.Clear();

        IRunnerInstance instance;
        try
        {
            // Staging is the backend's business — the process backend stages a private runner copy
            // here, the container backend does nothing with runnerBinDirectory at all. Doing that skip
            // in this method instead would have meant an "if (isContainer)", the one thing §6.4 forbids.
            instance = await backend.StartAsync(
                new RunnerStartRequest(app.Name, runnerBinDirectory, app.Path, app.Isolation, HandleMessageAsync)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Covers exactly the failure a CPU-starved box produces right after a patch reboot
            // (tiworker.exe pegging every core): the runner process can't get scheduled enough to
            // connect its pipe within ConnectTimeout, the backend's StartAsync throws, and — without
            // this — the application would just stay unstarted forever, since no runner instance ever
            // existed to fire Exited and trigger the crash-restart path below. Routed through the same
            // backoff/give-up machinery so a transient CPU storm gets retried until it passes, rather
            // than treated as a permanent failure on the first unlucky timeout.
            //
            // Fire-and-forget rather than awaited: this can run inside StartAsync's own initial
            // reconciliation pass over every assigned application, and the retry sequence below can
            // legitimately take minutes (backoff up to MaxAttempts). Awaiting it here would block every
            // OTHER application from even attempting its first start until this one's entire retry
            // sequence finished — exactly backwards during a CPU storm, where other apps might connect
            // fine on their first try.
            await _logSink.WriteAgentLogAsync($"{app.Name}: failed to start runner: {ex}").ConfigureAwait(false);

            // The backend may have staged a runner copy before it failed; the retry stages afresh.
            CleanupBackendArtifacts(app.Name);
            FireAndForget(() => ScheduleRetryOrGiveUpAsync(app, "failed to start"));
            return;
        }

        // Tracked even if discovery below fails — StopAsync/DisposeAsync still needs to find and
        // clean up a process that started but never finished reporting in.
        _instances[app.Name] = instance;
        state.StartedAtUtc = DateTimeOffset.UtcNow;
        instance.Exited += OnInstanceExited;

        // Job Object containment is ProcessRunnerBackend's business — AgentHost deliberately does not
        // know Job Objects exist.

        ReadyMessage ready;
        try
        {
            ready = await instance.WhenReady.WaitAsync(_options.ConnectTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _logSink.WriteAgentLogAsync($"{app.Name}: runner never completed discovery: {ex}").ConfigureAwait(false);

            // Unsubscribe first: DisposeAsync below kills the still-hung process, which fires
            // Process.Exited — but DisposeAsync also marks the stop as "requested" before it kills, so
            // OnInstanceExited would no-op anyway even without this. Belt and suspenders against ever
            // scheduling two retries for the one failure.
            instance.Exited -= OnInstanceExited;
            _instances.TryRemove(app.Name, out _);
            await instance.DisposeAsync().ConfigureAwait(false);
            CleanupBackendArtifacts(app.Name);

            // Same reasoning as the connect-failure catch above — must not block sibling applications.
            FireAndForget(() => ScheduleRetryOrGiveUpAsync(app, "never completed discovery in time"));
            return;
        }

        // Checked before anything acts on the Ready payload. A protocol mismatch is a configuration
        // problem, not a transient one — a differently-versioned runner binary will still be
        // differently-versioned on the next attempt — so it goes straight to Failed like a missing
        // runner-bin, rather than through the crash-backoff/retry machinery. Proceeding instead would
        // be the bad outcome the check exists to prevent: fields the peer never sent silently read as
        // null/default, and the application misbehaves far from the actual cause.
        if (RunnerProtocol.DescribeMismatch(ready.ProtocolVersion) is { } mismatch)
        {
            state.State = ApplicationState.Failed;
            await _logSink.WriteAgentLogAsync($"{app.Name}: {mismatch}").ConfigureAwait(false);

            instance.Exited -= OnInstanceExited;
            _instances.TryRemove(app.Name, out _);
            await instance.DisposeAsync().ConfigureAwait(false);
            CleanupBackendArtifacts(app.Name);

            await WriteStatusSnapshotAsync().ConfigureAwait(false);
            return;
        }

        foreach (var warning in ready.Warnings)
        {
            await _logSink.WriteAgentLogAsync($"{app.Name}: discovery warning: {warning}").ConfigureAwait(false);
        }

        if (ready.Isolation.DepsFilesFound == 0)
        {
            await _logSink.WriteAgentLogAsync(
                $"{app.Name}: isolation did not engage (no deps.json found) - a version collision with " +
                "the runner's own dependencies (there are none) is not the risk here, but this is still " +
                "worth knowing before treating this app as isolated.").ConfigureAwait(false);
        }

        state.Description = ready.ApplicationDescription;

        // Services are the "always on" half of an application — starting all of them the moment
        // discovery completes is what DesiredState.Running means at the application level. Jobs are
        // the scheduled half; they don't run now, they get REGISTERED now (see below).
        foreach (var service in ready.Services)
        {
            state.ServiceDescriptions[service.Name] = service.Description;
            await instance.SendAsync(new StartServiceCommand(service.Name)).ConfigureAwait(false);
        }

        // Idempotent — safe (and necessary) on a restart, where the previous registration for this
        // app's jobs is still live and would otherwise double-fire alongside the new one. Also resets
        // state.Jobs, which means a job paused via ExecuteCommandAsync does NOT survive a real restart
        // of this application (crash-retry, a Path/CronOverrides change) — it comes back active. That's
        // a deliberate first-cut trade: the pause is an imperative, in-memory action, not part of the
        // declarative assignment, so there's nowhere durable for it to be remembered across a restart
        // without promoting it into ApplicationPolicyDto — worth revisiting if that ever proves
        // surprising in practice.
        _scheduler!.UnregisterAll(app.Name);
        state.Jobs.Clear();

        foreach (var job in ready.Jobs)
        {
            var cron = app.CronOverrides.TryGetValue(job.Name, out var overridden) ? overridden : job.DeclaredCron;
            if (string.IsNullOrWhiteSpace(cron))
            {
                await _logSink.WriteAgentLogAsync(
                    $"{app.Name}/{job.Name}: no cron declared and no override configured - will only run if triggered manually.").ConfigureAwait(false);
                continue;
            }

            try
            {
                _scheduler.Register(app.Name, job.Name, cron);
                state.Jobs[job.Name] = new JobTrackedState { Cron = cron, Description = job.Description };
            }
            catch (FormatException ex)
            {
                await _logSink.WriteAgentLogAsync($"{app.Name}/{job.Name}: {ex.Message}").ConfigureAwait(false);
            }
        }

        state.State = ApplicationState.Running;
        await WriteStatusSnapshotAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Releases per-application artifacts across EVERY configured backend rather than looking up the one
    /// that started this application. Deliberate: teardown happens on paths where the assignment (and so
    /// the declared isolation mode) may no longer exist — a rule removed from the control plane is gone
    /// by the time its application is stopped. Cleanup is idempotent and cheap on a backend that has
    /// nothing to release, so asking all of them is both correct and simpler than tracking which one
    /// owned what, and it avoids AgentHost holding an opinion about backend identity.
    /// </summary>
    private void CleanupBackendArtifacts(string applicationName)
    {
        foreach (var backend in _runnerBackends.Values)
        {
            try
            {
                backend.Cleanup(applicationName);
            }
            catch
            {
                // Best-effort, exactly as RunnerStaging.Cleanup already was — a leftover staging folder
                // costs disk, never correctness, and must not fail a teardown.
            }
        }
    }

    /// <summary>Stops and fully tears down one running application — shared by StopAsync (every instance, on shutdown) and ReconcileAsync (one instance, because it was removed/deassigned/changed). A no-op when nothing is running. Callers hold the application's lifecycle gate.</summary>
    private async Task StopApplicationAsync(string name)
    {
        if (!_instances.TryRemove(name, out var instance))
        {
            return;
        }

        instance.Exited -= OnInstanceExited;
        _scheduler?.UnregisterAll(name);

        var exitedGracefully = await instance.StopAsync(_options.StopGracePeriod).ConfigureAwait(false);
        await _logSink.WriteAgentLogAsync(
            exitedGracefully ? $"{name}: stopped." : $"{name}: did not stop within {_options.StopGracePeriod.TotalSeconds:0}s - killed.").ConfigureAwait(false);

        await instance.DisposeAsync().ConfigureAwait(false);
        CleanupBackendArtifacts(name);

        if (_appState.TryGetValue(name, out var state))
        {
            state.State = ApplicationState.Stopped;

            // The runner process this app's services ran inside no longer exists — their last known
            // StateChangedMessage ("Running") would otherwise sit in ServiceStates forever, since
            // nothing else ever updates it once the process is gone. Left stale, the status report
            // (and the portal's per-service Start/Stop buttons) would keep showing every service as
            // Running under a Stopped application, implying live control over services that have no
            // process behind them anymore.
            foreach (var serviceName in state.ServiceStates.Keys.ToList())
            {
                state.ServiceStates[serviceName] = RunnerState.Stopped.ToString();
            }
        }
    }

    /// <summary>
    /// Fired by IRunnerInstance.Exited. wasRequested distinguishes StopAsync/DisposeAsync (nothing to
    /// do here — AgentHost's own stop path already handles that) from an actual crash — a process that
    /// disappeared without anyone asking it to. A plain void handler (not async void): the actual
    /// async work is handed to FireAndForget, which owns exception containment and shutdown tracking
    /// in one place rather than duplicating that per event handler.
    /// </summary>
    private void OnInstanceExited(IRunnerInstance instance, bool wasRequested)
    {
        if (wasRequested || _shutdownCts.IsCancellationRequested)
        {
            return;
        }

        var app = _currentAssignments?.Applications.FirstOrDefault(a => a.Name == instance.ApplicationName);
        if (app is null || app.DesiredState != DesiredState.Running)
        {
            // Shouldn't normally happen (nothing un-assigned should have a running instance to begin
            // with), but never restart something that isn't supposed to be running.
            return;
        }

        FireAndForget(() => HandleUnexpectedExitAsync(app, instance));
    }

    /// <summary>
    /// Runs a retry/restart sequence in the background, tracked so StopAsync can wait for every
    /// in-flight one to actually finish (or be cut short by _shutdownCts) before returning, and with
    /// its own exception containment — nothing calling this awaits the result, so an unhandled
    /// exception here has nowhere else to go except silently crashing the process whose entire job is
    /// keeping OTHER things alive, which would be exactly backwards.
    /// </summary>
    /// <summary>
    /// How many background retry/restart tasks are still in flight. Exists for one test: the entries
    /// are completed tasks when this leaks, so nothing waits longer and nothing fails - the only
    /// symptom is memory, on an agent that runs for months.
    /// </summary>
    internal int PendingRetryCount
    {
        get
        {
            lock (_pendingRetriesLock)
            {
                return _pendingRetries.Count;
            }
        }
    }

    /// <summary>Internal only so AgentDisposalTests can drive the tracking directly - see PendingRetryCount. Nothing outside this class calls it.</summary>
    internal void FireAndForget(Func<Task> action)
    {
        // The tracking handle is created and registered BEFORE any work starts, so it exists however
        // quickly the body finishes.
        //
        // This used to read `Task task = null!; task = RunSafelyAsync();` and register afterwards,
        // which is a race the body wins whenever it completes synchronously - and it does, routinely:
        // ScheduleRetryOrGiveUpAsync returns early for an application that has been deassigned or has
        // already given up, without ever awaiting anything that yields. The finally then ran
        // `Remove(null)`, which removed nothing, and the completed task was added a moment later and
        // never removed. _pendingRetries grew by one dead entry per early return, for the life of the
        // agent, and StopAsync waits on every task in it.
        var tracked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_pendingRetriesLock)
        {
            _pendingRetries.Add(tracked.Task);
        }

        _ = RunSafelyAsync();

        async Task RunSafelyAsync()
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _logSink.WriteAgentLogAsync($"Unhandled error in a background retry/restart task: {ex}").ConfigureAwait(false);
            }
            finally
            {
                lock (_pendingRetriesLock)
                {
                    _pendingRetries.Remove(tracked.Task);
                }

                // After the removal, so a StopAsync that snapshotted the list a moment earlier still
                // waits for this to finish rather than racing it.
                tracked.SetResult();
            }
        }
    }

    private async Task HandleUnexpectedExitAsync(ApplicationAssignment app, IRunnerInstance instance)
    {
        await _logSink.WriteAgentLogAsync(
            $"{app.Name}: runner exited unexpectedly (pid {instance.Pid}, exit code {DescribeExitCode(instance)}).").ConfigureAwait(false);

        var wasCurrent = false;
        await WithLifecycleGateAsync(app.Name, async () =>
        {
            // Only THIS instance's registry entry, and only if it is still the current one. A reconcile
            // that already replaced it must not have its new runner's jobs unregistered or its staging
            // deleted because the old one finally died.
            wasCurrent = _instances.TryRemove(new KeyValuePair<string, IRunnerInstance>(app.Name, instance));
            if (wasCurrent)
            {
                _scheduler?.UnregisterAll(app.Name);
                CleanupBackendArtifacts(app.Name);

                // Disposed only when this WAS the current instance. If it was not, a reconcile already
                // replaced it and already disposed it as part of StopApplicationAsync - and disposing
                // it a second time from here threw ObjectDisposedException out of CancelAsync on the
                // instance's own CancellationTokenSource, straight into FireAndForget's catch, where
                // it was logged as "unhandled error in a background retry/restart task" for something
                // that was in fact an orderly replacement. The instances are idempotent now as well,
                // but not calling it twice is the honest fix.
                await instance.DisposeAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

        if (wasCurrent)
        {
            await ScheduleRetryOrGiveUpAsync(app, "runner exited unexpectedly").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The single retry/give-up decision, shared by every failure category: a runner that never
    /// managed to start (connect timeout — see StartApplicationAsync's first catch, the tiworker.exe
    /// CPU-storm scenario), one that started but never reported Ready in time (second catch), and one
    /// that started fine and later crashed on its own (HandleUnexpectedExitAsync). All three are the
    /// same shape of problem from here on: something kept this application from running, and the
    /// question is only whether to back off and try again or give up. Callers are responsible for
    /// their own type-specific cleanup (disposing a half-started instance, removing it from _instances,
    /// etc.) before calling this — it only owns the counting, backoff and retry-or-give-up decision.
    /// </summary>
    private async Task ScheduleRetryOrGiveUpAsync(ApplicationAssignment app, string reason)
    {
        if (!_appState.TryGetValue(app.Name, out var state))
        {
            // Deassigned since this was scheduled — nothing left to retry.
            return;
        }

        if (state.StartedAtUtc is { } startedAt && _backoff.HasSettledSince(startedAt, DateTimeOffset.UtcNow))
        {
            // Ran fine for a while before this failure — a fresh problem, not a continuation of a streak.
            state.ConsecutiveFailures = 0;
        }

        state.ConsecutiveFailures++;
        state.RestartCount++;

        if (state.ConsecutiveFailures > _backoff.MaxAttempts)
        {
            state.State = ApplicationState.Failed;
            CleanupBackendArtifacts(app.Name);
            await _logSink.WriteAgentLogAsync(
                $"{app.Name}: giving up after {state.ConsecutiveFailures} consecutive failures within " +
                $"{_options.RestartSettledDuration.TotalMinutes:0} minute(s) of starting - not retrying again automatically.").ConfigureAwait(false);
            await WriteStatusSnapshotAsync().ConfigureAwait(false);
            return;
        }

        state.State = ApplicationState.Restarting;
        await WriteStatusSnapshotAsync().ConfigureAwait(false);

        var delay = _backoff.DelayFor(state.ConsecutiveFailures);
        await _logSink.WriteAgentLogAsync(
            $"{app.Name}: {reason} - retrying in {delay.TotalSeconds:0}s (attempt {state.ConsecutiveFailures}/{_backoff.MaxAttempts}).").ConfigureAwait(false);

        try
        {
            await Task.Delay(delay, _shutdownCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Re-read what is wanted NOW, not what was wanted when the failure happened. During the backoff
        // a reconcile may have disabled this application, changed its assignment, or restarted it
        // already — starting from the captured copy resurrected disabled applications and collided
        // with live ones, flipping a healthy application to Restarting.
        var current = _currentAssignments?.Applications.FirstOrDefault(a => a.Name == app.Name);
        if (current is null || current.DesiredState != DesiredState.Running)
        {
            await _logSink.WriteAgentLogAsync($"{app.Name}: no longer wanted running - retry abandoned.").ConfigureAwait(false);
            return;
        }

        if (_instances.ContainsKey(app.Name))
        {
            await _logSink.WriteAgentLogAsync($"{app.Name}: already running again - retry not needed.").ConfigureAwait(false);
            return;
        }

        // freshIntent: this IS an attempt the agent decided to make, not a re-examination of an
        // unchanged list, so its outcome is worth reporting even if it fails the same way as last time.
        await WithLifecycleGateAsync(app.Name, () => StartApplicationAsync(current, freshIntent: true)).ConfigureAwait(false);
    }

    private static string DescribeExitCode(IRunnerInstance instance)
    {
        try
        {
            return instance.ExitCode?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// The imperative counterpart to ReconcileAsync: acts on ONE target of ONE already-running
    /// application, immediately, without touching anything else that application is running.
    /// targetKind is "Service" or "Job", action is "Start" or "Stop" — see AgentCommandRequest for why
    /// enabling/disabling a job uses the same two verbs as service stop/start rather than its own vocabulary.
    /// Wrapped in its own try/catch since the only caller is a fire-and-forget SignalR event handler
    /// (see ControlPlaneAssignmentSource.CommandReceived) with nowhere else for an exception to go.
    /// </summary>
    public async Task ExecuteCommandAsync(string applicationName, string targetKind, string targetName, string action)
    {
        try
        {
            if (!_appState.TryGetValue(applicationName, out var state) || !_instances.TryGetValue(applicationName, out var instance))
            {
                await _logSink.WriteAgentLogAsync(
                    $"Command ignored: '{applicationName}' is not currently running on this machine.").ConfigureAwait(false);
                return;
            }

            switch (targetKind)
            {
                case AgentCommandRequest.Service:
                    // No explicit status push here — StopServiceCommand/StartServiceCommand round-trip
                    // through the runner and come back as a StateChangedMessage (see HandleMessageAsync),
                    // which already updates ServiceStates and pushes a snapshot once the change has
                    // actually happened, rather than this method optimistically claiming it already did.
                    await ExecuteServiceCommandAsync(applicationName, instance, targetName, action).ConfigureAwait(false);
                    break;

                case AgentCommandRequest.Job:
                    // Whether a job is enabled is agent-side state — JobScheduler owns when it fires,
                    // and no StateChangedMessage comes back to report it — so the change needs an
                    // explicit push here; nothing else will produce one.
                    await ExecuteJobCommandAsync(instance, state, applicationName, targetName, action).ConfigureAwait(false);
                    await WriteStatusSnapshotAsync().ConfigureAwait(false);
                    break;

                default:
                    await _logSink.WriteAgentLogAsync($"Command ignored: unknown target kind '{targetKind}'.").ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            await _logSink.WriteAgentLogAsync($"{applicationName}: command ({targetKind}/{targetName}/{action}) failed: {ex}").ConfigureAwait(false);
        }
    }

    private async Task ExecuteServiceCommandAsync(string applicationName, IRunnerInstance instance, string serviceName, string action)
    {
        switch (action)
        {
            case AgentCommandRequest.Start:
                await instance.SendAsync(new StartServiceCommand(serviceName)).ConfigureAwait(false);
                break;

            case AgentCommandRequest.Stop:
                await instance.SendAsync(new StopServiceCommand(serviceName, (int)_options.StopGracePeriod.TotalMilliseconds)).ConfigureAwait(false);
                break;

            default:
                await _logSink.WriteAgentLogAsync($"{applicationName}: command ignored - unknown action '{action}' for service '{serviceName}'.").ConfigureAwait(false);
                break;
        }
    }

    private async Task ExecuteJobCommandAsync(IRunnerInstance instance, AppTrackedState state, string applicationName, string jobName, string action)
    {
        if (!state.Jobs.TryGetValue(jobName, out var jobState))
        {
            await _logSink.WriteAgentLogAsync(
                $"{applicationName}: command ignored - job '{jobName}' is not registered (unknown, or declared with no cron and no override).").ConfigureAwait(false);
            return;
        }

        switch (action)
        {
            case AgentCommandRequest.Stop:
                _scheduler?.Unregister(applicationName, jobName);
                jobState.Paused = true;

                // Both halves are wanted, and only one of them is agent-side: no more firings, AND the
                // run currently underway is asked to unwind. Unregistering alone would leave a long
                // batch going for however long it had left, which is not what "stop" means to whoever
                // asked for it. Harmless when nothing is in flight — the runner says so and moves on.
                await instance.SendAsync(new CancelJobCommand(jobName)).ConfigureAwait(false);
                break;

            case AgentCommandRequest.Start:
                try
                {
                    _scheduler?.Register(applicationName, jobName, jobState.Cron);
                    jobState.Paused = false;
                }
                catch (FormatException)
                {
                    // The cron was valid when this job was first registered at app start — nothing
                    // should be able to make it invalid by the time a resume comes in, but if it
                    // somehow did, leaving Paused=true (rather than claiming success) is the honest
                    // reflection of what actually happened.
                }
                break;

            default:
                await _logSink.WriteAgentLogAsync($"{applicationName}: command ignored - unknown action '{action}' for job '{jobName}'.").ConfigureAwait(false);
                break;
        }
    }

    private async Task RunJobDueAsync(string applicationName, string jobName, CancellationToken ct)
    {
        if (!_instances.TryGetValue(applicationName, out var instance) || instance.HasExited)
        {
            await _logSink.WriteAgentLogAsync($"{applicationName}/{jobName}: due to run but its runner isn't available.").ConfigureAwait(false);
            return;
        }

        var jobState = _appState.TryGetValue(applicationName, out var state) && state.Jobs.TryGetValue(jobName, out var tracked) ? tracked : null;

        // No overlap. A run still in flight when the next tick comes means this tick is skipped, and
        // said in the application's own log; the alternative — a job slower than its schedule stacking
        // concurrent copies of itself without bound — is never what a schedule means. In-flight runs
        // are cleared by their result, by a Stop (the runner cancels and reports Cancelled), and by the
        // application restarting (its tracked state is rebuilt).
        if (jobState is not null && !jobState.InFlightRuns.IsEmpty)
        {
            var (inFlightId, startedAt) = jobState.InFlightRuns.OrderBy(r => r.Value).Select(r => (r.Key, r.Value)).First();
            await WriteSyntheticAsync(applicationName, LogLevel.Warning,
                $"job '{jobName}' was due, but run {inFlightId} started {(DateTimeOffset.UtcNow - startedAt).TotalSeconds:0}s ago is still running - this tick is skipped, not queued. Shorten the work or lengthen the schedule.").ConfigureAwait(false);
            return;
        }

        // Per-run settings beyond what the plugin's own colocated settings file provides (e.g.
        // assignment-level job parameters) are a future concern — nothing to source them from yet, so
        // an empty dictionary; SettingsLoader.Load still applies file-based settings on the runner
        // side regardless.
        var runId = Guid.NewGuid().ToString("N");
        jobState?.InFlightRuns.TryAdd(runId, DateTimeOffset.UtcNow);
        try
        {
            await instance.SendAsync(new RunJobCommand(jobName, runId, new Dictionary<string, string>()), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The runner went away between the check above and the send. Its exit is handled where
            // exits are handled; this firing is simply lost, and the loss is written down rather than
            // disappearing into the scheduler's catch-all.
            jobState?.InFlightRuns.TryRemove(runId, out _);
            await WriteSyntheticAsync(applicationName, LogLevel.Error,
                $"job '{jobName}' was due, but the run command could not be sent to its runner ({ex.Message}) - this firing is lost; the next one is tried normally.").ConfigureAwait(false);
        }
    }

    private async Task HandleMessageAsync(IRunnerInstance instance, RunnerMessage message)
    {
        var state = _appState.TryGetValue(instance.ApplicationName, out var s) ? s : null;
        var statusChanged = false;

        switch (message)
        {
            case LogMessage log:
                await _logSink.WriteAppLogAsync(instance.ApplicationName, log).ConfigureAwait(false);
                break;

            case StateChangedMessage sc:
                await WriteSyntheticAsync(instance.ApplicationName, LogLevel.Information, $"{sc.TargetKind} '{sc.Target}' -> {sc.State}").ConfigureAwait(false);
                if (state is not null && sc.TargetKind == RunnerTargetKind.Service)
                {
                    state.ServiceStates[sc.Target] = sc.State.ToString();
                    statusChanged = true;
                }
                break;

            case JobResultMessage jr:
                // Cancellation is deliberately NOT an error. Someone pressed Stop, or the agent is
                // shutting down; reporting that back as a failure tells an operator something went
                // wrong when what actually happened is that they got what they asked for. It is still
                // a Warning rather than Information, because the run did not finish its work and that
                // is worth noticing when reading the log back later.
                var (level, outcome, text) = jr.Outcome switch
                {
                    JobOutcome.Succeeded => (LogLevel.Information, JobRunOutcome.Succeeded,
                        $"job '{jr.Job}' run {jr.RunId} succeeded in {jr.DurationMs}ms"),
                    JobOutcome.Cancelled => (LogLevel.Warning, JobRunOutcome.Cancelled,
                        $"job '{jr.Job}' run {jr.RunId} cancelled after {jr.DurationMs}ms - stopped before it finished"),
                    _ => (LogLevel.Error, JobRunOutcome.Failed,
                        $"job '{jr.Job}' run {jr.RunId} FAILED in {jr.DurationMs}ms: {jr.Error}"),
                };

                await WriteSyntheticAsync(instance.ApplicationName, level, text).ConfigureAwait(false);
                if (state is not null && state.Jobs.TryGetValue(jr.Job, out var jobState))
                {
                    jobState.InFlightRuns.TryRemove(jr.RunId, out _);
                    jobState.LastRunUtc = DateTimeOffset.UtcNow;
                    jobState.LastRunOutcome = outcome;
                    statusChanged = true;
                }
                break;

            case FaultedMessage f:
                await WriteSyntheticAsync(instance.ApplicationName, LogLevel.Error, $"fault ({f.Target ?? "runner"}): {f.Error}").ConfigureAwait(false);
                break;

            case ReadyMessage:
                // Already consumed inline via instance.WhenReady in StartApplicationAsync.
                break;
        }

        if (statusChanged)
        {
            await WriteStatusSnapshotAsync().ConfigureAwait(false);
        }

        MessageReceived?.Invoke(instance, message);
    }

    private Task WriteSyntheticAsync(string applicationName, LogLevel level, string text) =>
        _logSink.WriteAppLogAsync(applicationName, new LogMessage("agent", level, text, DateTimeOffset.UtcNow));

    /// <summary>
    /// reportToken overrides the token the control-plane report is made with. It exists for exactly
    /// one caller — the final snapshot in StopAsync — because by then the shutdown token is already
    /// cancelled and reporting under it is a guaranteed no-op. See StopAsync.
    /// </summary>
    private async Task WriteStatusSnapshotAsync(CancellationToken? reportToken = null)
    {
        // One at a time, end to end: generating and reporting are both inside the lock, so a snapshot
        // taken earlier cannot land at the control plane after one taken later.
        await _snapshotLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await WriteStatusSnapshotCoreAsync(reportToken ?? _shutdownCts.Token).ConfigureAwait(false);
        }
        finally
        {
            _snapshotLock.Release();
        }
    }

    private async Task WriteStatusSnapshotCoreAsync(CancellationToken reportToken)
    {
        var apps = _appState.Select(kvp =>
        {
            var (name, state) = (kvp.Key, kvp.Value);
            _instances.TryGetValue(name, out var instance);

            return new ApplicationStatusEntry(
                name,
                state.State,
                instance?.Pid,
                state.RestartCount,
                state.ServiceStates.Select(s => new ServiceStatusEntry(s.Key, s.Value, state.ServiceDescriptions.GetValueOrDefault(s.Key))).ToList(),
                state.Jobs.Select(j => new JobStatusEntry(j.Key, j.Value.LastRunUtc, j.Value.LastRunOutcome, j.Value.Paused, j.Value.Cron, j.Value.Description)).ToList(),
                state.Description,

                // Read from the ASSIGNMENT, not the instance: it is the declared intent, so it stays
                // reportable even while the application is Failed or between restarts, when there is no
                // instance to ask. RuntimeId does come from the instance, and is simply absent when
                // nothing is running.
                _currentAssignments?.Applications.FirstOrDefault(a => a.Name == name)?.Isolation.Mode,
                instance?.RuntimeId,

                // From the INSTANCE, not the policy: the policy says which ports were asked for, the
                // instance knows which the engine actually bound — and for a dynamic mapping those are
                // different numbers.
                instance?.Endpoints);
        }).ToList();

        var snapshot = new AgentStatusSnapshot(DateTimeOffset.UtcNow, apps);
        await _statusWriter.WriteAsync(snapshot).ConfigureAwait(false);

        // Best-effort by construction (see ControlPlaneStatusReporter) — a report failing must never
        // block or fail anything else this method's callers are in the middle of doing.
        await _statusReporter.ReportAsync(snapshot, reportToken).ConfigureAwait(false);
    }

    private async Task RunRetentionSweepLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _logSink.PruneOldLogsAsync(_options.LogRetention).ConfigureAwait(false);

            try
            {
                await Task.Delay(_options.LogRetentionSweepInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Every other WriteStatusSnapshotAsync call site fires because something actually changed (a
    /// service/job state transition, a reconciliation pass). None of that happens while everything is
    /// simply running fine with nothing new to say — so without this loop, a healthy, quiet agent would
    /// go silent indefinitely, and anything watching report age (see the portal's staleness handling
    /// in RunningInstancesTable) would eventually — and wrongly — read that silence as the agent
    /// being gone. This sends the SAME snapshot shape on a timer regardless, purely so "last
    /// report" keeps advancing during otherwise-uneventful stretches.
    /// </summary>
    private async Task RunHeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.HeartbeatInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await WriteStatusSnapshotAsync().ConfigureAwait(false);
            await ReportCapabilitiesAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Re-probes and re-reports. Failures are swallowed: capability reporting is observability, and must never be able to stop an agent supervising its applications.</summary>
    private async Task ReportCapabilitiesAsync(CancellationToken ct)
    {
        try
        {
            await _statusReporter.ReportCapabilitiesAsync(await GetCapabilitiesAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // The reporter says its own HTTP failures (rate-limited); what reaches here is the probe
            // behind the capabilities, which is rare enough to say every time.
            await _logSink.WriteAgentLogAsync($"Capability report skipped - the capabilities could not be determined: {ex.Message}").ConfigureAwait(false);
        }
    }

    public async Task StopAsync()
    {
        // Idempotent, because DisposeAsync calls straight into here and callers legitimately do both:
        // `await using var host = ...` after an explicit StopAsync is the ordinary shape in the tests
        // and in Program.cs. A second pass used to cancel an already-disposed token source and close
        // the job object handle a second time.
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
        {
            return;
        }

        _shutdownCts.Cancel();

        // Cancelling above unblocks any pending retry's backoff Task.Delay so it returns without
        // restarting — but one could also be mid-StartApplicationAsync (actually spawning a process)
        // right now, and that needs to be waited out here, BEFORE the stop loop below, so a
        // freshly-added instance from a retry that was already past its backoff wait still gets
        // included in that loop rather than leaking past this StopAsync entirely.
        List<Task> pendingRetries;
        lock (_pendingRetriesLock)
        {
            pendingRetries = _pendingRetries.ToList();
        }

        try
        {
            await Task.WhenAll(pendingRetries).ConfigureAwait(false);
        }
        catch
        {
        }

        if (_reconciliationLoop is not null)
        {
            try
            {
                await _reconciliationLoop.ConfigureAwait(false);
            }
            catch
            {
            }

            _reconciliationLoop = null;
        }

        if (_retentionSweepLoop is not null)
        {
            try
            {
                await _retentionSweepLoop.ConfigureAwait(false);
            }
            catch
            {
            }

            _retentionSweepLoop = null;
        }

        if (_heartbeatLoop is not null)
        {
            try
            {
                await _heartbeatLoop.ConfigureAwait(false);
            }
            catch
            {
            }

            _heartbeatLoop = null;
        }

        if (_scheduler is not null)
        {
            await _scheduler.DisposeAsync().ConfigureAwait(false);
            _scheduler = null;
        }

        foreach (var name in _instances.Keys.ToList())
        {
            await WithLifecycleGateAsync(name, () => StopApplicationAsync(name)).ConfigureAwait(false);
        }

        // The LAST thing the control plane hears from this agent, and the one snapshot that must not
        // be reported under the shutdown token. _shutdownCts was cancelled at the top of this method,
        // so passing it here - as this did until 2026-09-12 - made the reporter throw immediately and
        // swallow it as "shutting down": the agent stopped every application and then never said so.
        // The portal went on showing them Running until the staleness window expired minutes later.
        //
        // Bounded independently instead, because shutdown cannot wait on an unreachable control plane.
        using (var finalReport = new CancellationTokenSource(FinalSnapshotTimeout))
        {
            await WriteStatusSnapshotAsync(finalReport.Token).ConfigureAwait(false);
        }

        if (_assignmentSource is IAsyncDisposable disposableSource)
        {
            await disposableSource.DisposeAsync().ConfigureAwait(false);
        }

        // After the final snapshot and after every application's last log line, so the flush on the
        // way out has everything to carry. Nothing disposed the forwarder before 2026-09-12, so its
        // "final best-effort flush on shutdown" never ran and the last flush interval of forwarded
        // lines - which is where "stopped." lives - was dropped on every clean shutdown. Disposed
        // here for the same reason the assignment source is: this host is what owns its lifetime once
        // it has been handed one.
        if (_logForwarder is IAsyncDisposable disposableForwarder)
        {
            await disposableForwarder.DisposeAsync().ConfigureAwait(false);
        }

        // Every instance above already exited (gracefully or killed) before this runs, so nothing is
        // still assigned to the job — closing the handle here is tidy cleanup, not a kill trigger. If
        // this process were instead to crash before reaching this line, THAT is exactly the case the
        // job object exists for: the OS closes the handle on our behalf and takes every runner with it.
        if (_jobObject is not null && OperatingSystem.IsWindows())
        {
            _jobObject.Dispose();
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private sealed class AppTrackedState
    {
        public string State = ApplicationState.Stopped;
        public int RestartCount;
        public int ConsecutiveFailures;
        public DateTimeOffset? StartedAtUtc;
        public string? Description;

        // Written by this application's own receive loop and read by the snapshot from other contexts.
        public readonly ConcurrentDictionary<string, string> ServiceStates = new();
        public readonly ConcurrentDictionary<string, string?> ServiceDescriptions = new();
        public readonly ConcurrentDictionary<string, JobTrackedState> Jobs = new();
    }

    /// <summary>Cron is remembered here specifically so ExecuteCommandAsync can resume a paused job without needing the full ApplicationAssignment/ready-discovery context that originally registered it.</summary>
    private sealed class JobTrackedState
    {
        public required string Cron;
        public bool Paused;
        public DateTimeOffset? LastRunUtc;
        public string? LastRunOutcome;
        public string? Description;

        /// <summary>Run id → when it was dispatched. Non-empty means a tick arriving now is skipped (see RunJobDueAsync).</summary>
        public readonly ConcurrentDictionary<string, DateTimeOffset> InFlightRuns = new();
    }
}
