using System.Net.Http.Json;

using Enlist.ControlPlane.Contracts;

using Microsoft.AspNetCore.SignalR.Client;

namespace Enlist.Agent.Configuration;

/// <summary>
/// Fetches this agent's applicable policy rules from Enlist.ControlPlane over REST, and learns about
/// changes by dialing OUT to its SignalR hub (design doc section 6 — never inbound) rather than
/// polling on a fixed interval. A push carries no payload (see ApplicationPolicyHub) —
/// WaitForChangeAsync just unblocks, and the caller re-fetches via GetCurrentAsync, so the REST
/// contract stays the one source of truth for what a policy rule actually contains.
///
/// The push is the fast path, never the only path. Three things keep that true: the hub reconnects for
/// as long as it takes rather than giving up after SignalR's stock four attempts (~42 seconds, after
/// which an agent used to sit detached for the rest of its life while still heartbeating over HTTP and
/// reading as Online); a connection the server closed outright is reopened from here; and
/// WaitForChangeAsync returns on a timer as well as on a push, so a push lost to a dead connection, or
/// a fetch that failed at push time, costs at most one refetch interval. The state of the push
/// connection is reported alongside capabilities (IReportsPushChannel) so the portal can show it.
///
/// Also resolves package-based rules to a local path via PackageCache. Known limitation: if resolving
/// ONE application's package fails (network blip, bad digest), the whole GetCurrentAsync call throws
/// rather than skipping just that one — AgentHost's reconciliation loop already catches and retries on
/// the next change signal, so this degrades to "nothing reconciles until the next push, instead of
/// everything except the broken app," which is an acceptable trade for the added complexity a per-app
/// partial-failure path would need. Worth revisiting if it turns out to matter.
///
/// Implements IPrunesPackageCache rather than pruning inline in GetCurrentAsync — AgentHost calls
/// PruneUnusedPackagesAsync only AFTER ReconcileAsync has run, which is what actually stops whatever
/// process was using a now-superseded digest's files. Pruning any earlier lost a real race: a
/// redeploy's OLD runner is still holding its plugin dll open (loaded, in some cases memory-mapped)
/// until StopApplicationAsync finishes, so a delete attempted before that point fails on a file lock
/// and gets silently swallowed by PruneUnusedAsync's own best-effort try/catch — the digest just never
/// gets cleaned up, quietly, with nothing in any log to explain why.
///
/// Also relays imperative AgentCommandRequests (CommandReceived) over this same hub connection —
/// there's exactly one outbound connection per agent by design (section 6: dials out, never accepts
/// inbound), so a second command-only HubConnection would just be two sockets doing the one job this
/// class already does for policy-changed pushes.
/// </summary>
public sealed class ControlPlaneAssignmentSource : IAssignmentSource, IAsyncDisposable, IPrunesPackageCache, IReportsPushChannel
{
    /// <summary>The floor under push: how long the reconciliation loop waits for a push before re-fetching anyway. One small GET per agent per interval is negligible load; a change lost to a dead connection is not lost for the day.</summary>
    public static readonly TimeSpan DefaultRefetchInterval = TimeSpan.FromMinutes(3);

    private readonly HttpClient _http;
    private readonly HubConnection _hub;
    private readonly string _agentName;
    private readonly PackageCache _packageCache;
    private readonly TimeSpan _refetchInterval;

    // Cancelled first thing in DisposeAsync so the reopen loop in the Closed handler stops, and so that
    // handler can tell a disposal (its own Closed event) from a connection the server ended.
    private readonly CancellationTokenSource _lifetime = new();

    // Released on every push AND on every successful (re)connect — a connection that just came back
    // up may have missed pushes while it was down, so reconnecting is treated the same as "something
    // might have changed, go check" rather than assuming nothing did.
    private readonly SemaphoreSlim _changeSignal = new(0);

    private HashSet<string> _lastDigestsInUse = new(StringComparer.OrdinalIgnoreCase);

    private volatile string _pushChannelState = PushChannelStates.Disconnected;

    /// <summary>Fired for every AgentCommandRequest pushed to this agent. Not coalesced like the change signal above — each one is a distinct imperative action, not a "go re-check" nudge.</summary>
    public event Action<AgentCommandRequest>? CommandReceived;

    public event Action? PushChannelStateChanged;

    public event Action<string>? Diagnostic;

    public string PushChannelState => _pushChannelState;

    public ControlPlaneAssignmentSource(Uri controlPlaneBaseUrl, string agentName, string packageCacheRoot, TimeSpan? refetchInterval = null)
    {
        _agentName = agentName;
        _refetchInterval = refetchInterval ?? DefaultRefetchInterval;
        _http = new HttpClient { BaseAddress = controlPlaneBaseUrl };
        _packageCache = new PackageCache(_http, packageCacheRoot);

        _hub = new HubConnectionBuilder()
            .WithUrl(new Uri(controlPlaneBaseUrl, ApplicationPolicyHubContract.HubPath))
            .WithAutomaticReconnect(new PersistentReconnectPolicy())
            .Build();

        _hub.On(ApplicationPolicyHubContract.ApplicationPoliciesChangedMethod, () => ReleaseChangeSignal());
        _hub.On<AgentCommandRequest>(ApplicationPolicyHubContract.ExecuteCommandMethod, command => CommandReceived?.Invoke(command));

        _hub.Reconnecting += error =>
        {
            SetPushChannelState(
                PushChannelStates.Reconnecting,
                $"Control plane push connection lost ({Describe(error)}) - reconnecting until it returns. " +
                $"Policy changes still arrive on the {_refetchInterval.TotalMinutes:0}-minute re-check meanwhile.");
            return Task.CompletedTask;
        };

        // WithAutomaticReconnect gets the connection itself back up, but SignalR groups are keyed by
        // ConnectionId — a reconnect hands out a NEW one, so without re-joining here this agent's group
        // membership from ConnectAsync's original JoinAgentGroupMethod call is gone. Every
        // ApplicationPoliciesChanged push sent after that point lands in a group with nobody actually
        // listening (Clients.Group().SendAsync() doesn't error on an empty group, so this fails
        // silently): the agent looks connected and healthy, but never learns about another policy
        // change until something else happens to trigger a re-check. The catch-up fetch in
        // RejoinAndCatchUpAsync still covers whatever changed WHILE this connection was down, but that's
        // a one-time check at reconnect time, not a substitute for actually being reachable for the
        // next push.
        _hub.Reconnected += _ => RejoinAndCatchUpAsync("restored");

        // The policy above never gives up, so Closed means the server ended the connection outright
        // (or this object is being disposed) — reopen it from here rather than leave the agent detached.
        // Deliberately NOT applied to a failed initial ConnectAsync: that still throws, so a service
        // started while the control plane is down fails to start and the service manager restarts it
        // (Deployment-IaC §1.6), instead of reporting itself started with nothing to run.
        _hub.Closed += error =>
        {
            if (_lifetime.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }

            SetPushChannelState(PushChannelStates.Disconnected, $"Control plane push connection closed ({Describe(error)}) - reopening until it succeeds.");
            return ReopenUntilConnectedAsync();
        };
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        await _hub.StartAsync(ct).ConfigureAwait(false);
        await _hub.InvokeAsync(ApplicationPolicyHubContract.JoinAgentGroupMethod, _agentName, ct).ConfigureAwait(false);
        SetPushChannelState(PushChannelStates.Connected, message: null);
    }

    public async Task<AgentAssignments> GetCurrentAsync(CancellationToken ct)
    {
        var dtos = await _http.GetFromJsonAsync<List<ApplicationPolicyDto>>($"/api/agents/{Uri.EscapeDataString(_agentName)}/policies", ct).ConfigureAwait(false)
            ?? new List<ApplicationPolicyDto>();

        var applications = new List<ApplicationAssignment>();
        var digestsInUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dto in dtos)
        {
            // A conflicted resolution (see ApplicationPolicyDto.ConflictReason) has no single winning
            // Path/PackageDigest to resolve — there's nothing to extract, and AgentHost.StartApplicationAsync
            // checks ConflictReason before ever looking at Path, so an empty placeholder here is never
            // actually used.
            if (dto.ConflictReason is not null)
            {
                applications.Add(new ApplicationAssignment
                {
                    Name = dto.ApplicationName,
                    Path = "",
                    DesiredState = Enum.Parse<DesiredState>(dto.DesiredState),
                    CronOverrides = new Dictionary<string, string>(dto.CronOverrides),
                    RuntimeFlavor = dto.RuntimeFlavor,
                    Isolation = dto.Isolation ?? IsolationSpec.ProcessDefault,
                    ConflictReason = dto.ConflictReason,
                });
                continue;
            }

            // A digest resolves deterministically to the same local folder every time (see
            // PackageCache), which is what makes AgentHost's existing "did Path change" reconciliation
            // check also correctly detect a package upgrade — no separate digest-comparison logic
            // needed there, this resolution step is the only place that has to know digests exist.
            var path = dto.PackageDigest is not null
                ? await _packageCache.EnsureExtractedAsync(dto.PackageDigest, ct).ConfigureAwait(false)
                : dto.Path ?? throw new InvalidOperationException(
                    $"Policy rule for '{dto.ApplicationName}' has neither Path nor PackageDigest set.");

            if (dto.PackageDigest is not null)
            {
                digestsInUse.Add(dto.PackageDigest);
            }

            applications.Add(new ApplicationAssignment
            {
                Name = dto.ApplicationName,
                Path = path,
                DesiredState = Enum.Parse<DesiredState>(dto.DesiredState),
                CronOverrides = new Dictionary<string, string>(dto.CronOverrides),
                RuntimeFlavor = dto.RuntimeFlavor,
                Isolation = dto.Isolation ?? IsolationSpec.ProcessDefault,
            });
        }

        // Remembered for PruneUnusedPackagesAsync, called later by AgentHost — NOT pruned here. See
        // the class summary for why "later" (after reconciliation stops old processes) is load-bearing.
        _lastDigestsInUse = digestsInUse;

        return new AgentAssignments { Applications = applications };
    }

    /// <summary>Reclaims every locally cached digest not in the set this class's own last GetCurrentAsync call observed — safe to call any time after that, in particular once AgentHost's reconciliation has actually stopped whatever was using a superseded one.</summary>
    public Task PruneUnusedPackagesAsync(CancellationToken ct) => _packageCache.PruneUnusedAsync(_lastDigestsInUse, ct);

    /// <summary>Completes on a push, on a (re)connect, or when the refetch interval runs out — whichever is first. A fetch that finds nothing changed costs one small GET and a no-op reconcile, which is the price of never depending on the push alone.</summary>
    public Task WaitForChangeAsync(CancellationToken ct) => _changeSignal.WaitAsync(_refetchInterval, ct);

    private async Task RejoinAndCatchUpAsync(string how)
    {
        try
        {
            await _hub.InvokeAsync(ApplicationPolicyHubContract.JoinAgentGroupMethod, _agentName, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Connected but not in the group is exactly "not receiving", so the state is left as it was
            // rather than declared connected; the next Reconnected or Closed takes it from here, and the
            // refetch floor covers the gap.
            Diagnostic?.Invoke($"Control plane push connection {how}, but re-joining the agent group failed ({ex.Message}) - waiting for the next reconnect; the periodic re-check covers the gap.");
            return;
        }

        SetPushChannelState(PushChannelStates.Connected, $"Control plane push connection {how} - re-checking policy.");
        ReleaseChangeSignal();
    }

    private async Task ReopenUntilConnectedAsync()
    {
        var attempt = 0L;
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PersistentReconnectPolicy.DelayFor(attempt), _lifetime.Token).ConfigureAwait(false);
                await _hub.StartAsync(_lifetime.Token).ConfigureAwait(false);
                await RejoinAndCatchUpAsync("reopened").ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                attempt++;

                // The first failure and then every tenth — an outage measured in hours must not fill
                // the agent log with one identical line per attempt.
                if (attempt == 1 || attempt % 10 == 0)
                {
                    Diagnostic?.Invoke($"Control plane push connection still closed after {attempt} reopen attempt(s): {ex.Message}");
                }
            }
        }
    }

    private void SetPushChannelState(string state, string? message)
    {
        var changed = _pushChannelState != state;
        _pushChannelState = state;

        if (message is not null)
        {
            Diagnostic?.Invoke(message);
        }

        if (changed)
        {
            PushChannelStateChanged?.Invoke();
        }
    }

    private static string Describe(Exception? error) => error is null ? "closed by the server" : error.Message;

    // SemaphoreSlim.Release throws if the count would exceed its max — capped at 1 pending signal
    // (coalescing bursts of changes into a single re-fetch) rather than queuing one release per push.
    private void ReleaseChangeSignal()
    {
        if (_changeSignal.CurrentCount == 0)
        {
            _changeSignal.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await _hub.DisposeAsync().ConfigureAwait(false);
        _http.Dispose();
        _changeSignal.Dispose();
        _lifetime.Dispose();
    }

    /// <summary>
    /// Never returns null, which is what SignalR reads as "give up". The stock policy stops after four
    /// attempts (0, 2, 10, 30 seconds) — shorter than a database migration or a host reboot — and an
    /// agent that outlived that window stayed detached for good. This ramps to a 30-second interval and
    /// stays there: patient enough not to hammer a control plane that is down, quick enough that one
    /// coming back is noticed within a half minute.
    /// </summary>
    private sealed class PersistentReconnectPolicy : IRetryPolicy
    {
        private static readonly TimeSpan[] Ramp =
        [
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(30),
        ];

        public static TimeSpan DelayFor(long previousAttempts) => Ramp[(int)Math.Min(previousAttempts, Ramp.Length - 1)];

        public TimeSpan? NextRetryDelay(RetryContext retryContext) => DelayFor(retryContext.PreviousRetryCount);
    }
}
