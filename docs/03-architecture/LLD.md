# Low-Level Design (LLD)

**Product:** enList v3
**Document status:** Derived from the current implementation. Originally 2026-09-03; **§3, §11 and §12 rewritten 2026-09-08** against the shipped code, after an audit found tag-matching pseudocode that no longer resembled the real functions, a file path (`AppInstancePanel.razor`) that no longer exists, and a toggling flow still branching on explicit-versus-selector targeting. Class/method-level detail beneath [`HLD.md`](HLD.md). Line numbers are omitted deliberately (they drift); file paths are exact as of this writing.

---

## 1. Content-Addressed Package Storage — `PackageBlobStore.SaveAsync`

**File:** `src/Enlist.ControlPlane/Data/PackageBlobStore.cs`

```
1. Create a temp file under the blob root.
2. Stream the request body through a CryptoStream(SHA256) into that temp file —
   hashing happens WHILE writing, one pass, no separate read-back.
3. FlushFinalBlockAsync to finalize the hash; digest = lowercase hex of the SHA-256 hash.
4. If <root>/<digest>.zip already exists: delete the temp file, return (digest, existingFileLength, AlreadyExisted=true).
5. Else: File.Move (atomic on the same volume) the temp file to <root>/<digest>.zip, return (digest, size, AlreadyExisted=false).
```

**Invariants:** the digest is always computed from bytes actually received, never supplied by the caller. A partially-written upload never becomes visible under its final digest name (the temp file only gets its name once fully written and hashed).

## 2. Package Version Numbering — `NextVersionNumberAsync`

**File:** `src/Enlist.ControlPlane/Program.cs`

```csharp
static async Task<int> NextVersionNumberAsync(ControlPlaneDbContext db, string applicationName)
{
    var highest = await db.Packages
        .Where(p => p.ApplicationName == applicationName)
        .Select(p => (int?)p.VersionNumber)
        .MaxAsync();
    return (highest ?? 0) + 1;
}
```

Computed as **(highest existing VersionNumber for this application name) + 1** — deliberately *not* "current count of packages still in the table." The latter would silently renumber every surviving package whenever an older one is garbage-collected, which would make a previously-known "v3" quietly become "v2." A gap in the sequence (v1 deleted, only v2/v3 remain) is acceptable; a renumbered v3 is not.

`ApplicationName`/`VersionNumber` are set **first-write-wins**: a package uploaded once anonymously (no `?application=`) and later re-uploaded (same bytes, now with `?application=X`) backfills the name/version onto the *existing* row rather than creating a second row or renaming anything already labeled.

## 3. Tag Selector Matching

**Server-side** (`src/Enlist.ControlPlane/Program.cs`):

```csharp
static bool SelectorMatches(IReadOnlyDictionary<string, string> tags, IReadOnlyDictionary<string, string> selector)
{
    foreach (var (key, value) in selector)
    {
        if (!tags.TryGetValue(key, out var actual) || actual != value) return false;
    }

    return true;
}
```

**Portal-side mirror** (`src/Enlist.Portal/Services/TagSelectorMatcher.cs`):

```csharp
public static bool Matches(AgentDto agent, IReadOnlyDictionary<string, string> selector) =>
    selector.All(kv => agent.Tags.TryGetValue(kv.Key, out var value) && value == kv.Value);
```

Both are the same predicate — **every key in the selector must be present in the agent's tags with an equal value**; an empty selector matches every agent; extra tags beyond the selector's keys are irrelevant. Two implementations exist only because the portal resolves matches client-side for aggregate counts across a full rule list, without one round trip per agent. They are kept in sync by inspection — there is no shared assembly enforcing it, since `Enlist.Portal` does not reference `Enlist.ControlPlane`.

### 3.1 The implicit self-tag

There is **no explicit-agent targeting**. Every agent behaves as though it carries an additional `agent=<name>` tag that is never stored:

```csharp
var effectiveTags = EffectiveAgentTags(agentName, agent?.TagsJson ?? "{}");
```

"Run on exactly this one agent" is therefore the *most specific possible tag selector*, not a second mechanism competing with tags. That collapse is what removed a whole class of ambiguity — the old model had to decide which of an explicit placement and a matching selector won.

### 3.2 Matching, then resolving — two steps

```csharp
static async Task<List<ApplicationPolicyEntity>> ResolveMatchingPoliciesForAgentAsync(
    ControlPlaneDbContext db, string agentName, AgentEntity? agent)
{
    if (agent is { SchedulingEnabled: false })
    {
        return [];                                  // excluded agents match nothing at all
    }

    var effectiveTags = EffectiveAgentTags(agentName, agent?.TagsJson ?? "{}");
    var candidates = await db.ApplicationPolicies.ToListAsync();
    return candidates.Where(a => SelectorMatches(effectiveTags, DeserializeTags(a.TagSelectorJson))).ToList();
}
```

That returns the **raw** match set, which may contain several rules for one application. `ResolveEffectivePoliciesForAgentAsync` then collapses it to at most one outcome per `ApplicationName`, because the agent's reconciliation needs exactly one answer:

- Rules that **agree** on `DesiredState`, `Path`, `PackageDigest`, `CronOverrides` **and `Isolation`** resolve to the first of them — they are equivalent, so the choice is arbitrary and safe. This is the common case: a canary rule and a fallback rule that happen to reference the same build right now.
- Rules that **disagree** on any of those produce a `ConflictReason` naming the rule IDs, rather than a silently-chosen winner. There is no correct answer to give the agent, and picking one would start the application the wrong way with no indication why.

Two details worth keeping:

**`Isolation` is compared with `AgreesWith`, never `==`.** `IsolationSpec` is a positional record whose `Ports`/`Networks`/`Env` collections compare by **reference** under compiler-generated equality, so `==` reports two structurally identical specs as different — which in reconciliation means restarting the application on every pass, forever.

**The exclusion short-circuit needs no agent-side code.** An excluded agent resolves to an empty list, and the agent's existing reconciliation already tears down everything absent from the list it is given.

**Shared by both callers** — `GET /api/agents/{name}/policies` (what the agent reconciles against) and `GET /api/application-policies?agentName=` (what the portal shows) — so the two can never disagree about who matches what. An earlier defect where they *did* disagree is written up in [`Runbook.md` §3.4](../05-operations/Runbook.md).

## 4. Crash Backoff — `CrashBackoff`

**File:** `src/Enlist.Agent/Supervision/CrashBackoff.cs`

```csharp
public TimeSpan DelayFor(int attempt) // attempt is 1-based
{
    var seconds = _baseDelay.TotalSeconds * Math.Pow(2, Math.Max(0, attempt - 1));
    return TimeSpan.FromSeconds(Math.Min(seconds, _maxDelay.TotalSeconds));
}
```

Default construction: `maxAttempts=5, baseDelay=2s, maxDelay=60s, settledDuration=2min` → delays of **2s, 4s, 8s, 16s, 32s** (attempt 6 would compute 64s but is never reached — attempt 5 is the last retry before giving up).

**Settle reset:** `HasSettledSince(startedAt, now) => now - startedAt > settledDuration`. In `AgentHost.ScheduleRetryOrGiveUpAsync`, if the instance that just crashed had been running longer than `settledDuration` (2 minutes) before it crashed, `ConsecutiveFailures` resets to 0 first — a crash after a long healthy run is treated as a fresh problem, not a continuation of an old crash streak. Only **rapid, repeated** failures count against `MaxAttempts`; once `ConsecutiveFailures > MaxAttempts`, the application is marked `Failed` and not retried again automatically.

## 5. JobScheduler Registration Race

**File:** `src/Enlist.Agent/Scheduling/JobScheduler.cs`

`Register`/`Unregister` for a given `(ApplicationName, JobName)` key can be invoked from two independent call paths at genuinely the same time:

- `AgentHost`'s reconciliation loop (`StartApplicationAsync`, driven by an `ApplicationPoliciesChanged` signal).
- `AgentHost.ExecuteCommandAsync` (driven by an independent, fire-and-forget SignalR command push).

Without synchronization, this can leave **two** `RunLoopAsync` loops alive for the same key simultaneously — observed as a job firing twice on the same scheduled tick right after a disable/enable racing a redeploy. `JobScheduler` guards its internal `Dictionary<(string App, string Job), Registration>` with a private `object _lock`, taken around every Register/Unregister, closing this race.

### 5.1 Firing semantics

`RunLoopAsync` waits for one occurrence at a time, in sleeps of at most a day (`maxSleep`) — `Task.Delay` refuses anything past about 49.7 days, and a yearly cron computes exactly that; the throw used to fault the loop unobserved. It fires only once the clock has passed the occurrence, and the next search starts strictly after it: a timer that wakes a fraction of a millisecond early (the delay is truncated to whole milliseconds) used to fire, recompute the same occurrence, and fire again — duplicate run commands, which the overlap policy turned into visible skip lines during live verification. A callback that throws is reported through `onDiagnostic` (the agent log) and the job is tried again next time; a loop that stops for any other reason says so the same way. Parsing is `CronExpressions` (Contracts): five fields or six, `?` for `*` — the same definition the control plane validates against.

Overlap is decided in `AgentHost.RunJobDueAsync`, not in the scheduler: `JobTrackedState.InFlightRuns` holds every dispatched run id until its `JobResultMessage` arrives. A tick that finds it non-empty is skipped and written to the application's log as a Warning naming the running run; a run command that cannot be sent removes its id again and is logged as a lost firing. The set is rebuilt with the tracked state whenever the application (re)starts, so a runner that died mid-run cannot leave a phantom in-flight entry behind.

## 6. Runner Plugin Discovery — Attribute Matching by Name

**File:** `src/Enlist.Runner/Discovery/PluginDiscovery.cs`

```csharp
private static CustomAttributeData? FindAttribute(MemberInfo member, string simpleName, List<string> warnings)
{
    foreach (var data in member.GetCustomAttributesData())
    {
        if (data.AttributeType.Name != simpleName) continue;
        if (data.AttributeType.Namespace != "Enlist")
            warnings.Add($"{DescribeMember(member)}: found {data.AttributeType.FullName}, expected Enlist.{simpleName}. " +
                         "Matched by name and used anyway, but this usually means a copy/paste picked up the wrong namespace.");
        return data;
    }
    return null;
}
```

Matching is by **`AttributeType.Name` string equality**, not `is EnlistServiceAttribute` or `IsAssignableFrom` — deliberately, because the runner has no compiled reference to `contracts/EnlistAttributes.cs` at all; each plugin project compiles its **own copy** of that source file. A namespace mismatch is reported as a warning and still honored (fail-soft), rather than silently producing an empty Services/Jobs list.

Constructor and named arguments (`Name`, `Description`, `Cron`, etc.) are read via `CustomAttributeData.ConstructorArguments`/`NamedArguments` — reflection-only metadata inspection, not attribute instantiation, so this works even when the attribute type identity differs across assemblies.

Assembly-level `[EnlistApplication(Description=...)]` is found the same way, scanning `Assembly.GetCustomAttributesData()`; the **first** match across all loaded assemblies in the application directory wins (`applicationDescription ??= FindApplicationDescription(assembly)`).

## 7. Runner ↔ Agent Wire Protocol — `MessageChannel<TOut,TIn>`

**File:** `src/Enlist.Runner/Protocol/MessageChannel.cs`

- Transport: `System.IO.Pipes` (`NamedPipeClientStream`/`NamedPipeServerStream`) — BCL only, no package dependency (consistent with the runner's zero-`PackageReference` posture).
- Framing: **one JSON object per line**, UTF-8, no BOM. `System.Text.Json` escapes embedded newlines within string values, so a multi-line log message still serializes to exactly one physical line.
- `SendAsync` is guarded by a `SemaphoreSlim(1,1)` write lock — multiple logical senders (the outbound message pump, direct sends) share one physical stream and must not interleave partial writes.
- `ReceiveAsync` returns `default`/`null` at end of stream rather than throwing — the caller (`RunnerHost.RunAsync`) treats a null receive **identically** to an explicit `ShutdownCommand`: stop every running service, then exit. This is why there is no orphaned-runner state — closing the pipe (parent process gone) is functionally a shutdown request.

**Message types, runner → agent** (`RunnerMessage` hierarchy, polymorphic on a `"kind"` discriminator):
`ReadyMessage` (once, after discovery) · `LogMessage` · `StateChangedMessage` · `JobResultMessage` · `FaultedMessage`.

**Message types, agent → runner** (`AgentCommand` hierarchy):
`StartServiceCommand` · `StopServiceCommand` · `RunJobCommand` · `CancelJobCommand` · `ShutdownCommand`.

## 8. Agent Background Loops

**File:** `src/Enlist.Agent/AgentHost.cs`, started from `StartAsync`:

| Loop | Trigger | Body |
|---|---|---|
| `RunReconciliationLoopAsync` | `IAssignmentSource.WaitForChangeAsync` unblocks (SignalR push, reconnect, or the 3-minute re-fetch floor) | `GetCurrentAsync` → `ReconcileAsync` → `PruneAssignmentSourcePackageCacheAsync`. |
| `RunRetentionSweepLoopAsync` | `Task.Delay(LogRetentionSweepInterval)` (default 24h) | `_logSink.PruneOldLogsAsync(LogRetention)` (default 14 days) — local on-disk log files, independent of the control plane's own forwarded-log retention. |
| `RunHeartbeatLoopAsync` | `Task.Delay(HeartbeatInterval)` (default 2 min) | Unconditional `WriteStatusSnapshotAsync()` — see §9. |

All three are cooperatively cancelled via one `CancellationTokenSource` (`_shutdownCts`) and individually awaited (with exceptions swallowed) during `StopAsync`, in a fixed order: pending crash-retries → reconciliation loop → retention sweep loop → heartbeat loop → scheduler disposal → stop every remaining running instance → one final status snapshot.

### 8.1 Concurrency model

`AgentHost` is entered from several independent contexts at once — the loops above, crash-retries on the thread pool, `Process.Exited`, the SignalR command handler, and one receive loop per running application (`HandleMessageAsync`) — and `WriteStatusSnapshotAsync` enumerates the host's registries from the heartbeat loop *and* from every one of those receive loops. Three rules make that safe, and they are stated once on the fields they protect:

1. **Every shared registry is a concurrent collection** — `_instances`, `_appState`, and each tracked application's `ServiceStates`/`ServiceDescriptions`/`Jobs` — so enumeration never throws and a write from one context never corrupts another's. Per-field writes on a tracked state (`Paused`, `LastRunOutcome`, `State`) are benign.
2. **The lifecycle of one application is serialized through that application's gate** (`WithLifecycleGateAsync`, a `SemaphoreSlim` per name): start, stop, restart, and crash cleanup for one application never interleave. Per application deliberately, not host-wide — a thirty-second start of one application must not stall every other application's message handling. A changed assignment's stop-then-start happens under one hold of the gate, so a crash-retry cannot slip in between and start the old assignment.
3. **Snapshots are generated and reported under one lock**, so an older snapshot cannot overtake a newer one on the way to the control plane.

Two consequences worth knowing:

- **A crash-retry re-reads what is wanted after its backoff**, from `_currentAssignments`, rather than starting from the assignment it captured when the crash happened. During the backoff a reconcile may have disabled the application (it stays down), changed it (the retry starts the current version), or restarted it already (the retry stands down, logged as "already running again"). Before this, a retry resurrected disabled applications and collided with live ones.
- **`HandleUnexpectedExitAsync` removes only its own instance** (`TryRemove` of the exact key/value pair). If a reconcile has already replaced the runner that just died, the old one's death must not unregister the new one's jobs or delete its staging.

The receive loops on both `IRunnerInstance` implementations follow the same posture as the runner's own dispatch loop: **a handler that throws on one message is logged and the next message is read.** The loop ends only when the channel itself closes or fails — and a channel failing while the runner is still alive is logged as such, because from that point the agent can no longer hear a runner that is still running.

## 9. Heartbeat — Why It Exists

**File:** `src/Enlist.Agent/AgentHost.cs` (`RunHeartbeatLoopAsync`), `AgentHostOptions.HeartbeatInterval` (default 2 minutes).

Every other call site of `WriteStatusSnapshotAsync` is **reactive** — it fires because a service/job state actually changed, or a reconciliation pass ran. A perfectly healthy application with nothing happening (no crash, no job due, no command received) would otherwise never produce a new report, and the portal's staleness check (report age > 5 minutes ⇒ "Stale") would eventually — and incorrectly — flag it as unreachable. The heartbeat sends the same snapshot shape on a fixed timer regardless of activity, purely to keep "last report" advancing during quiet stretches. 2 minutes is chosen to sit comfortably under the portal's 5-minute threshold, tolerating at least one missed beat (a transient network blip) before a false "Stale" reading would appear.

## 10. Stopping an Application — Service-State Cleanup

**File:** `src/Enlist.Agent/AgentHost.cs` (`StopApplicationAsync`)

```
1. Remove and stop-await the runner instance (graceful stop within StopGracePeriod, then kill).
2. Unregister every scheduled job for this application from JobScheduler.
3. Dispose the instance; clean up its staging directory.
4. state.State = ApplicationState.Stopped.
5. For every key currently in state.ServiceStates: set its value to RunnerState.Stopped.ToString().
```

Step 5 exists because `ServiceStates` is populated purely from `StateChangedMessage` events while the application was running; without it, a service's last-known "Running" entry would remain in every subsequent status snapshot forever (the runner process — and the ability to ever send another `StateChangedMessage` for it — no longer exists once the application is stopped), which would make the portal's per-service Start/Stop buttons look actionable and "Running" under a `Stopped` application.

## 11. Portal Client-Side Staleness and Command-Gating

**File:** `src/Enlist.Portal/Components/Panels/RunningInstancesTable.razor`

```csharp
private bool CommandsDisabled(InstanceRow row) =>
    row.Stale
    || _statusByAgent.GetValueOrDefault(row.AgentName)
           ?.Applications.FirstOrDefault(a => a.Name == ApplicationName)?.State != "Running";
```

Gating is **per row**, not per panel — a row is one (service-or-job × agent) pair, and the same application can be live on one agent and stale on another, so a single panel-wide flag could not be right for both.

- `Stale` is derived from the agent's `LastSeenUtc` (or the status snapshot's own `GeneratedAtUtc`) against the same 5-minute threshold as the Online/Offline chip. That `LastSeenUtc` comes from the panel's **own** `_liveAgents` read, never from the `Agents` parameter: the Applications page loads that list once and does not poll it, so on a page left open past the threshold every agent in it reads as stale. The panel therefore re-reads agents in `OnInitializedAsync` — before the first row is built, not on the poll loop's first tick — so expanding an application cannot flash a staleness warning on healthy agents and then retract it three seconds later.
- Per-service/per-job imperative commands (Start/Stop for a service, Enable/Disable for a job) are disabled when **either** the agent hasn't reported recently **or** the application isn't currently `Running` there. In both cases the command's destination is a **runner process** relayed through the agent — if the application isn't running on that agent, that process does not exist, so enabling the button would accept a click that silently goes nowhere.
- Each disabled state carries its own tooltip (`CommandDisabledReason`) rather than a generic one, because the two causes have different fixes: *"This agent hasn't reported recently…"* versus *"This application isn't running on this agent…"*.

The app-level declarative Start/Stop is deliberately **not** gated this way — see §12.

> The predecessor `AppInstancePanel` / `AgentDetailPanel` / `ApplicationDetailPanel` components this section used to describe were removed in the five-tabs-to-three restructure; `RunningInstancesTable` and its Agents-tab counterpart `AgentRunningApplicationsTable` replaced them.

## 12. Policy Rule Toggling — Confirm Before Fan-Out

**File:** `src/Enlist.Portal/Services/ApplicationPolicyStateToggler.cs`

```
ToggleAsync(policy, newState):
    if policy.TagSelector is non-empty:
        agents  = GET /api/agents
        matching = TagSelectorMatcher.ResolveAgentNames(agents, selector)
        show confirmation dialog NAMING those agents,
             with the caveat that current AND FUTURE matching agents are affected
        if not confirmed: return false
    PUT /api/application-policies/{id} { DesiredState = newState }
    return true
```

There is no longer an explicit-versus-selector branch — targeting is tags only (§3.1), so every rule can in principle govern more than one agent, and every toggle therefore gets the same confirmation.

The confirmation exists because the blast radius is not visible from the button. A rule reading `env=prod` may look like one application in the UI while governing forty agents, and `DesiredState` is declarative — flipping it to `Stopped` stops the application *everywhere the selector reaches*, including on agents that begin matching later. Naming the current matches makes the immediate effect concrete; the "and future" caveat is what stops that list being mistaken for the complete one.

> **Not gated on liveness, deliberately.** Unlike the per-service commands in §11, this only writes `DesiredState` to the control plane's database. That is a real, always-effective action whether or not any matching agent is currently reachable — an offline agent picks it up when it reconnects.

---

## 13. Authorization — `EndpointPolicies` and `EndpointPolicyHandler`

*Added 2026-09-13. Built 2026-09-11; the LLD had no section on it, which for the one algorithm that decides who may do what is the worst place to have a gap.*

Authorization is a **table lookup with a fail-closed default**, not an attribute per endpoint. `EndpointPolicies.Table` is keyed by `(HTTP method, route template as written)` and returns one of six policies:

| Policy | Satisfied by |
|---|---|
| `Anonymous` | anyone. Only `GET /health`. |
| `JoinToken` | a live join token. Only `POST /api/agents/enroll`. |
| `Agent` | an agent credential **whose name equals the agent named in the route**. |
| `AgentOrViewer` | any agent credential or any management key. Package download, which both need. |
| `Viewer` | any management key, either role. Every read. |
| `Operator` | a management key carrying the `Operator` role. Every write. |

### 13.1 The default is refusal

`EndpointPolicies.For` returns `null` for a route that is not in the table, and every path the handler cannot decide — no `HttpContext`, no `RouteEndpoint`, no route template, no table entry — returns **without calling `context.Succeed`**. Not succeeding *is* the refusal: ASP.NET Core's authorization middleware then answers 401 to a caller with no credential and 403 to one with the wrong kind or role.

That is the load-bearing design decision in this whole area. Adding a route and forgetting to authorize it produces a 403 that someone notices immediately, not a public API that nobody notices at all. The cost is that the table must be edited whenever a route is added, which is the intended friction: it is one screen, and reviewing it answers "who can do what" completely.

### 13.2 The agent name binding

`EndpointPolicy.Agent` is the only policy that inspects the request beyond its route template. It reads `agentName` (or `name`) from the route values and compares it to the `agent` claim on the credential, **case-insensitively, because that is how the database compares agent names**. An agent credential is therefore not a fleet-wide pass: `DEV-AGENT-02`'s credential cannot fetch `DEV-AGENT-07`'s policies, reports or logs.

A mismatch here is a 403 rather than a 404, deliberately. Hiding the existence of another agent from a holder of a valid agent credential buys nothing — every agent already sees other agents' names in ordinary operation — and a 404 would send whoever is debugging it looking for a registration problem that does not exist.

### 13.3 The hub is decided separately

SignalR's negotiate and connection endpoints are matched by path prefix (`EndpointPolicies.IsHub`) rather than by table entry, because their route templates are the framework's, not this application's. Any agent credential may connect. **Which group a connection may join is checked inside the hub**, not here: group membership is a per-connection call (`JoinAgentGroup`), not a route, so it is outside what a route table can express at all.

### 13.4 What is audited, and what is not

`AuditMiddleware` reuses this same table to decide what to record — one structured line per administrative **write**, naming method, path, outcome and who. It skips reads (`GET`/`HEAD`/`OPTIONS`), the hub, and anything whose policy is `Agent` — an agent acting as itself is telemetry arriving every few seconds from every agent, and auditing it would bury every line worth reading. Enrollment is audited: it is the one thing a join token does.

A write that **throws** is audited too, from a `catch` that re-throws. It is the line most worth having and, until 2026-09-12, the only one that produced nothing at all — the exception unwound straight past the logging call. No status code is reported on that path, deliberately: the exception handler upstream has not run yet, so `Response.StatusCode` is still whatever it was before the failure, usually 200, which would be a lie in an audit trail.
