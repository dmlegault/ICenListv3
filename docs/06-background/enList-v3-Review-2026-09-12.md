# enList v3 — Full Regression Review, 12 September 2026

**Status:** Findings only. **Nothing in this document has been fixed yet.** No code was
changed during the review; the working tree was clean at `34007c9`.

**How this was produced.** Seven reviewers read every line of their area: control plane +
contracts, agent, both runners (file-by-file drift diff), portal + deploy + samples, all
test projects, architecture + requirements docs, and ops docs + scripts + config. The
seventh needed four attempts; its findings are in §8 and in a companion document.

**Confidence.** Line numbers are as reported at `34007c9` and will drift as fixes land.
Items marked **[V]** were verified directly against the source during consolidation;
everything else is a reviewer's claim and must be re-read before acting on it. Several
"bugs" below are judgement calls about intent, not proven defects — treat the severity as
a starting point for triage, not a verdict.

---

## 1. Fix first (correctness, security, data loss)

**Fixed so far:** items 1, 7 and 8 (`a48da7a`), the command-ordering defect below (`75f9665`),
items 2, 3, 4, 15, 16 and 17 (`8e17af8`), and items 5, 6, 9, 12, 18, 21, 22 and 23 (`794aae9`).

**Still open in this section: items 10, 11, 13, 14, 19, 20 and 24.**

The table keeps every finding's original wording, because what a finding said when it was found is
the useful record; the commits carry the detail of what changed. Two things learned along the way are
worth more than any single fix:

- **A comment asserting a guarantee is not a guarantee.** The ordering defect survived review because
  the field's doc comment described the property the code was supposed to have, and read as a
  decision already made rather than an intention.
- **A test of a race has to say how many times it looked.** One start/stop pair caught the ordering
  defect a quarter of the time and passed the rest, so the suite reported green while the guarantee
  went unverified. Several of the concurrency tests added since are opportunistic for the same
  reason, and now say so in place rather than implying more than they prove.

| # | Where | What | Fix |
|---|---|---|---|
| 1 | `ControlPlane/Authentication/AuthenticationSetup.cs:57-75` + `Contracts/Hosting/ListenerRules.cs:26-29` | **[V]** `ConfiguredUrls` reads only `urls` and `Kestrel:Endpoints:*:Url`. It cannot see `ASPNETCORE_HTTP_PORTS`/`HTTPS_PORTS` (which bind `http://*:port`) or IIS bindings, and `ListenerRules` then assumes the Kestrel default `http://localhost:5000`. So `Authentication:Mode=Off` + `ASPNETCORE_HTTP_PORTS=8080` starts an **unauthenticated control plane on all interfaces** — the one thing the hard rule exists to prevent | Read `HTTP_PORTS`/`HTTPS_PORTS`; better, check `IServerAddressesFeature` after start so the rule sees what Kestrel actually bound. Same gap in the portal's private copy, `Portal/Services/PortalAuthentication.cs:60-78` **[V]** |
| 2 | `Agent/Logging/AgentFileLogSink.cs:73-83` | `AppendAsync` has no serialization and `File.AppendAllTextAsync` opens `FileShare.Read`; concurrent writes to one file throw a sharing violation that `catch {}` swallows. **Log lines silently vanish under load** | Serialize per path (a `SemaphoreSlim`, as `AgentStatusWriter` already does) or hold one append stream |
| 3 | `Agent/AgentHost.cs:1280` (with 1209, 1142) | `StopAsync` cancels `_shutdownCts`, then passes that cancelled token to the final `WriteStatusSnapshotAsync`; the reporter drops it as "shutting down". The **last "Stopped" snapshot is never sent**, so the portal shows apps Running until staleness expires | Report the final snapshot with `CancellationToken.None` or a short independent timeout |
| 4 | `Agent/AgentHost.cs:331, 453-490, 815-823` | The 3-minute refetch completes `WaitForChangeAsync`, so reconcile runs even when nothing changed: every Stopped app re-logs, every Failed app re-logs and re-POSTs, and an app that gave up ("not retrying again automatically") is **restarted through a fresh backoff cycle every 3 minutes, forever** | Act and log only on a transition; an unchanged Failed/Stopped app is a no-op on a refetch pass |
| 5 | `Runner/Execution/RunnerHost.cs:213-222` + `Runner.Legacy/…:216-225` | A failing `CreateInstance` after `StateChanged(Starting)` sends only `FaultedMessage`, never `StateChanged(Faulted)` (unlike the bind/Start path at :239-245). The agent logs the fault but the **service sits at "Starting" forever** | Send `StateChangedMessage(..., Faulted)` first, as the catch below does. Both runners, one commit |
| 6 | `Runner.Legacy/Protocol/MessageChannel.cs:69-115` | **Drift.** `ReceiveAsync(ct)` never uses `ct` (the modern copy threads it at :72/:114), so on net472 a cancelled processExit never unblocks the receive loop: the `catch (OperationCanceledException)` at RunnerHost:100-103 is unreachable and `DevDriver.ForceStop` cannot interrupt | Race the read against the token and throw OCE, matching net10.0 |
| 7 | `ControlPlane/Authentication/AuditMiddleware.cs:33` | **[V]** `await _next(context)` is not wrapped, so an administrative write that **throws produces no audit line at all** — precisely the requests worth auditing | `try/finally` around `_next` |
| 8 | `ControlPlane/Authentication/AuditMiddleware.cs:57,63` | **[V]** The client-supplied `X-Enlist-Operator` header goes verbatim into the audit line and `CreatedBy` (only `.Trim()`). CR/LF or unbounded length lets any Operator-key holder **forge audit lines** | Strip control characters, cap length |
| 9 | `ControlPlane/Program.cs:227-253` | Enrollment has no concurrency control: two agents on a `UsesRemaining=1` token both decrement (double spend); two first-time enrollments of one name both take the Add path and the loser gets a PK-violation 500 instead of 409 | Conditional `ExecuteUpdate` on `UsesRemaining > 0`; catch `DbUpdateException` → 409 |
| 10 | `Portal/Components/Panels/RunningInstancesTable.razor:123` | Guard `context.Type != "Conflict"` never matches — the pseudo-row is built at :283 with `Type = "—"`. The command button renders on the conflict row and **can send a Stop for a target named "Policy conflict"** | Add an `IsConflict` flag to `InstanceRow` |
| 11 | `Portal` wizard/edit/tags dialogs (`ApplicationPolicyWizardDialog.razor:220-221,315-318`, `ApplicationPolicyEditDialog.razor:227-229`, `EditTagsDialog.razor:45-47`) | `ToDictionary` on user rows throws `ArgumentException` on a duplicate key. Not an `ApiException`, so nothing catches it: **the circuit dies** | Validate duplicate/blank keys before building |
| 12 | `Agent/AgentHost.cs:733-757` | `Task task = null!; task = RunSafelyAsync();` — when the action completes synchronously the `finally` runs `Remove(null)` before assignment, then the completed task is added at :738 and never removed. `_pendingRetries` grows without bound | Register before starting |
| 13 | `Agent/AgentHost.cs:779` + `Supervision/ContainerRunnerInstance.cs:199,219` | `HandleUnexpectedExitAsync` disposes the instance even when `wasCurrent` is false; `DisposeAsync` then calls `CancelAsync` on a disposed CTS and throws into `FireAndForget`'s catch | Dispose only when `wasCurrent`, or make both `DisposeAsync` idempotent |
| 14 | `Agent/AgentHost.cs:1297`, `Configuration/ControlPlaneAssignmentSource.cs:301-308`, `Hosting/JobObject.cs:76-82` | `DisposeAsync() => StopAsync()` is not idempotent: a second pass cancels a disposed CTS and `CloseHandle`s the job handle twice | Guard with a stopped flag; null the handle after closing |
| 15 | `Agent/Logging/ControlPlaneLogForwarder.cs:115-127` | Nothing ever calls `DisposeAsync` (verified across src and tests), so the "final best-effort flush on shutdown" never runs — the last flush interval of lines, including every "stopped." line, is lost | Dispose it from `AgentHost.StopAsync` |
| 16 | `Agent/AgentHost.cs:261-262` | The initial `GetCurrentAsync`/`ReconcileAsync` are unguarded unlike the loop at :401-414: one bad package digest at boot throws out of `StartAsync`, the hosted service fails and **the SCM restart-loops the whole agent, starting nothing** | Catch and log like the loop does |
| 17 | `Agent/Configuration/PackageCache.cs:74-85` | Pruning enumerates `*.complete` markers only, so an interrupted extraction (directory and/or zip, no marker) is **never reclaimed** — the comment at :84 claims otherwise | Enumerate directories and zips, not markers |
| 18 | `ControlPlane/Data/PackageBlobStore.cs:30-47` | A failed `CopyToAsync` leaves an orphan `.tmp` nothing sweeps; two concurrent uploads of identical bytes race `!File.Exists` and the loser's `File.Move` throws → 500 for a package that *is* stored | Delete temp in catch; treat the `IOException` as `AlreadyExisted` |
| 19 | `Agent/Supervision/CliContainerEngineBase.cs:56-62` | The child CLI is killed only on the internal timeout; when the caller's `ct` cancels, the OCE propagates with the CLI still running (a leaked `docker wait` on shutdown) | Kill in `finally` when not exited |
| 20 | `Agent/Supervision/DockerContainerEngine.cs:143`, `WslContainerEngine.cs:188` | `stop --timeout <grace>` is itself bounded by the fixed 60 s command timeout; a `StopGracePeriod` above ~55 s has the agent kill the CLI before the engine escalates, then report "killed" while the container still runs | Use `gracePeriod + CommandTimeout` for this call |
| 21 | `Runner/Execution/RunnerHost.cs:500-517` | `PumpOutboundAsync` catches only OCE/IOException; anything else (e.g. `ObjectDisposedException` on a torn-down stream) faults the pump, `await pump` rethrows out of `RunAsync`, and every later Log/Send queues into a channel nobody drains | Catch `Exception`, log to stderr, stop cleanly |
| 22 | `Runner/Execution/RunnerHost.cs:272-283` | On `[EnlistStop]` overrun the service is removed and the still-running stop task is dropped; its eventual completion or fault is never reported, so a lone `StopServiceCommand` leaves it "Stopping" permanently | Continue the invocation and report Stopped/Faulted when it finishes |
| 23 | `Runner/Execution/RunnerHost.cs:358-360,474-477` | A `RunJobCommand` with no `settings` gives `Settings == null`; `MergeSettings` throws NRE *after* `StateChanged(Starting)`, so **no `JobResultMessage` ever follows** and the agent's in-flight entry never clears | Treat null overrides as empty, or merge inside the try |
| 24 | `Tests/Enlist.Agent.Tests/JobOverlapTests.cs:13` | `IAsyncDisposable` is never invoked by xunit 2.9.3 (two other test classes already record this measured finding). Cleanup never runs: **a live runner and a temp dir leak every run** | Use `IAsyncLifetime` like every sibling class |

### A command-ordering defect, found and fixed on 2026-09-12 — FIXED

While verifying the section 1 security fixes the full suite failed on a test nobody had touched,
reproducing roughly **one run in four**:

```
Enlist.Runner.Tests.ConcurrentCommandTests.Start_then_stop_for_one_service_are_applied_in_order
System.OperationCanceledException : The operation was canceled.
   at Enlist.Runner.Protocol.MessageChannel`2.ReceiveAsync(...) MessageChannel.cs:line 72
```

**I first attributed this to item 22 above. That was wrong**, and the correction is the interesting
part. The cause was `RunnerHost`'s command dispatch: each command ran on its own `Task.Run`, and the
per-service `SemaphoreSlim` that was supposed to keep Start and Stop in order granted only mutual
EXCLUSION. The two tasks raced to reach the semaphore, and when the Stop won it found nothing in
`_runningServices`, logged "not running - ignored" and returned silently, sending no state message at
all. The Start then reported `Running`. A service that had been told to stop sat in `Running` with no
terminal message ever sent.

The field's own doc comment claimed the guarantee the code did not provide, which is how it survived
review: the comment read as a decision already made.

Fixed by establishing order where it actually exists. The read loop is the only place that knows
what arrived first, so it now extends a per-service task chain synchronously instead of handing both
commands to the pool and hoping. Both runners changed in one commit, per the drift discipline.

Item 22 remains open and unproven: the stop-overrun path really does drop the still-running stop
task, but that is not what this flake was.

**The wider lesson, worth more than the fix.** That test is the regression guard for ordering, and a
single start/stop pair caught the defect only about one run in four. The other three runs it passed,
so the suite reported green while the guarantee went unverified. It now repeats twelve cycles, which
turns a 25% chance of catching a regression into roughly 97%, and fails by name on the
"not running - ignored" signature instead of blocking twenty seconds to report only that a token was
cancelled. **Any test whose subject is a race needs to say how many times it looked.**

## 2. Stale comments and doc-comments (source)

Authentication is done; a lot of prose still says it isn't.

- **[V]** `ControlPlane/Program.cs:142` — `/health` "Unauthenticated like everything else here (review finding C2)". It is the *one* `Anonymous` row in `EndpointPolicies`.
- `ControlPlane/Program.cs:474-475` — "until there's a portal". The portal exists and drives these endpoints.
- `Portal/Services/ApplicationPolicyStateToggler.cs:8-9` — **[V]** "the (Phase 1) ApplicationPolicies page and the agent detail panel". That page was folded away; the agent panel is read-only; the sole caller is `ApplicationPolicyScreen`.
- `Deploy/Enlist.Deploy.csproj:12-17` — "create-or-update an assignment for the target machine(s) or label selector". Three retired terms and behaviour the tool no longer has.
- `ControlPlane/Program.cs:899, 936-943` — two orphaned `<summary>` blocks describing the *next* method down (`NextVersionNumberAsync`, `DetectRuntimeFlavor`), one of which ("not called concurrently, so no extra locking") contradicts the retry loop beside it.
- `ControlPlane/Data/ControlPlaneDbContext.cs:56` and `Contracts/ApplicationPolicyDto.cs:22` — both cite `ResolveEffectivePoliciesAsync`, which does not exist (it is `…ForAgentAsync`). `ApplicationPolicyDto` also says "two or more **enabled** rules"; policies have no Enabled flag.
- `ControlPlane/Program.cs:1103` — runtime `ConflictReason` says "resolve by editing or **disabling** one of them"; a policy cannot be disabled. Say "deleting".
- `ControlPlane/Program.cs:1354-1355` — a post-mortem anecdote about a fixed bug ("Every LegacySample rule on this project's own demo was in exactly that state"). Delete.
- `Agent/Supervision/IRunnerBackend.cs:11-13` — claims AgentHost owns staging; staging moved into `ProcessRunnerBackend` and now rides the retry path.
- `Agent/Logging/AgentFileLogSink.cs:7-8` — "nothing to forward to yet". It forwards through `ILogForwarder`.
- `Agent/Configuration/AgentAssignments.cs:6-9` — "the step-2 stand-in for the control plane's eventual Assignments table".
- `Agent/Supervision/IRunnerInstance.cs:8-13`, `ProcessRunnerInstance.cs:14-15` — "the only implementation"; `ContainerRunnerInstance` exists.
- `Agent/AgentHost.cs:1166` — "see AppInstancePanel's Stale indicator"; no such component.
- `Runner/Protocol/UnixSocketChannelListener.cs:9-11`, `NamedPipeChannelListener.cs:9-10` — "chosen for the container case"; containers use `--listen` TCP.
- `Runner/Protocol/MessageChannel.cs:14-15`, `RunnerMessage.cs:10`, `IChannelListener.cs:11-13` — "the future agent"; the agent exists and references the runner project.
- `Runner.Legacy/Program.cs:12-14,71-73` — usage prints `enlist-runner-legacy`; the csproj deliberately names the exe `enlist-runner`.
- `Portal/Components/Dialogs/ApplicationPolicyEditDialog.razor:116-119`, `Panels/LogTailView.razor:36-37`, `Layout/NavMenu.razor:24-25` ("three links" — there are four), `Layout/MainLayout.razor:87` (same).
- `Portal/Components/Dialogs/EnrollAgentDialog.razor:28` — "(or the installer's Agent page)"; no installer exists yet.
- Retired vocabulary for the current concept: `ControlPlane/Data/PackageEntity.cs:41-42`, `PackageRetentionSweepService.cs:10,17-18,50`, `Contracts/AgentLogDto.cs:3`, `Agent/Status/AgentStatusSnapshot.cs:30-33`, `Agent/Configuration/PackageCache.cs:13`, `Agent/AgentHost.cs:889`, `Runner*/Discovery/DiscoveredJob.cs:13`.
- Build-phase references nobody can resolve now: `ControlPlane/Data/ApplicationPolicyEntity.cs:21`, `Program.cs:1358` ("the original step-3 shape"); `Contracts/ApplicationPolicyDto.cs:57` ("in this first cut"); `Runner.Legacy/Protocol/RunnerMessage.cs:40`, `Hosting/LegacyPluginLoadContext.cs:9-10`.
- Deferrals living as comments rather than tracked work: `Agent/AgentHost.cs:606-611, 1006-1009`.

## 3. Dead code

`ControlPlane`: `AuthenticationOptions.IsKnownMode` (:22, no callers); unused usings in `AuditMiddleware.cs:3`, `Data/ApiKeyEntity.cs:1`.

`Agent`: `CrashBackoff.Default` (:23-27); `AgentCredentialStore.Delete()` (:94-100); `ProcessRunnerInstance.JobObjectAssigned` (:58, written never read); duplicate `KillQuietly` in `ProcessRunnerInstance.cs:246-258` and `CliContainerEngineBase.cs:67-80`; unused usings at `AgentHost.cs:7`, `DockerContainerEngine.cs:1`, `WslContainerEngine.cs:6`; `_lifecycleGates` never releases a semaphore for a removed app (`AgentHost.cs:61,376`).

`Runner` (both): `MessageChannel._stream` assigned never read; `RunningServiceState.StartTask` awaited on the next line; `LegacyPluginLoadContext._appPath`; the `SetsRequiredMembers` polyfill in `Runner.Legacy/IsExternalInit.cs:29-33` (nothing uses it).

`Portal`: four template-leftover usings in `Components/_Imports.razor`; `ApplicationPolicyScreen.OnlyAgentValue` re-implements `TagSelectorMatcher`; two different `Group()` helpers (`Access.razor:121`, `Shared/AccessDenied.razor:25`) with different fallback text.

`Tests`: **`Enlist.Deploy.Tests` references `Enlist.Agent` and no test uses it** (csproj:22-26,36), with a comment describing a pipeline test that does not exist. `AuthenticationTests.Secret()` duplicates `ControlPlaneTestServer.CreateJoinTokenAsync`/`CreateApiKeyAsync`.

**Duplication worth consolidating into TestSupport** (the single biggest cleanup in the suite): a polling helper copy-pasted **seven** ways plus ~8 inline deadline loops; `Receive<T>`/`ReceiveUntil<T>`/`DrainUntilExitAsync` across 7 runner test classes **with two different semantics** (first-message-must-be-T vs skip-until-T — the former would break if a plugin logged at module-init); docker CLI helpers in 4 classes; `Authentication__Mode=Required` in 5; agent-log readers in 5.

## 4. Consistency and correctness-adjacent

- **Case sensitivity, three places.** `ControlPlane/Program.cs:1008,1017` compares the implicit self-tag `{"agent": name}` case-sensitively while agent names are case-insensitive everywhere else (SQL collation, `EndpointPolicyHandler:66`, `ApplicationPolicyHub:35`) — so `{"agent":"web-07"}` never matches `WEB-07`. `Contracts/IsolationSpec.cs:49` compares `Protocol` by record equality (`tcp` ≠ `TCP`) while validation accepts either case, producing phantom conflicts and missed port collisions. Portal filters use ordinal `==` where the server groups `OrdinalIgnoreCase` (5 sites).
- **Portal mirrors drifting from the authority they mirror.** `PolicyConflictDetector.CanonicalIsolation` drops `PortMapping.Name` and compares case-sensitively, so it flags what the server accepts and vice versa; the wizard's hypothetical rule omits `Isolation` entirely, previewing "agrees" where the server will flag a conflict.
- **Performance on the hot path.** `Program.cs:1341` allocates a `JsonSerializerOptions` per agent per request (STJ caches metadata *per instance*); `:1293-1298` runs one "latest report" query per agent — on the endpoint feed a reverse proxy polls continuously.
- **400s that surface as 500s.** `CredentialIssuer.Expiry.Parse` overflows on `"99999999999h"`; `CreateApiKeyAsync` races the filtered unique index; revoke matches an untrimmed name that create stored trimmed.
- **Silently ignored input.** `Agent/Program.cs:60-92` and `Deploy/Program.cs:124-133` both ignore unknown flags and a trailing flag with no value — a typo'd `--api_key` sends no key and the 401 blames the wrong thing.
- `Location` headers on three `Created` responses point at routes that have no GET.
- `Portal/Components/Pages/_Imports.razor:3` puts `[Authorize(Policy=Viewer)]` on `Error.razor` and `NotFound.razor`, so a non-admitted user gets AccessDenied instead of the error page.
- Cancellation tokens available but not passed: `EnlistBearerHandler` (5 DB calls), half of `AccessEndpoints`, and every portal poll loop (so `DisposeAsync` blocks on an in-flight call).

## 5. Project rule: ASCII in runtime strings

**~30 violations, all in the portal**, all in strings a user sees at runtime (em dash, `…`, `→`, `·`, and a pseudo-row whose `Type` is literally `"—"`). Files: `Agents.razor`, `Applications.razor`, `ApplicationPackagesScreen.razor`, `ApplicationPolicyScreen.razor`, `RunningInstancesTable.razor`, `AgentRunningApplicationsTable.razor`, `UploadPackageDialog.razor`, `ApplicationPolicyEditDialog.razor`, `ApplicationPolicyWizardDialog.razor`. No violations anywhere in the control plane, agent, runners, tests or scripts **[V]**.

Separately, **razor markup** carries ~25 more em dashes. That is user-visible text the rule has so far tolerated. **Decide once**: extend the rule to markup, or write the exemption down.

## 6. Documentation

**Wrong against the code** (highest value — these actively mislead):

1. `docs/README.md:34` tells a newcomer the authentication design is "not yet built". **[V]**
2. `API-Specification.md:263` — "`/health` … Unauthenticated, like every other endpoint". **[V]**
3. `API-Specification.md §7` DTOs are behind Contracts on four types: `HealthDto.Authentication`, `AgentDto.CredentialState`, `PackageInfo.RuntimeFlavor`, `UploadPackageResponse.RuntimeFlavor`; the `/health` JSON example omits `authentication` although §0 says it is there.
4. **Four documents** (SAD:147, SRS:113, LLD:105, Developer-Setup-Guide:193) say both policy endpoints route through `ResolveEffectivePoliciesForAgentAsync`. The portal-facing `?agentName=` endpoint deliberately calls the raw `ResolveMatchingPoliciesForAgentAsync` instead.
5. `enlist-deploy` documented as "only --control-plane, --app and --source … its entire surface" in three places; `--api-key`/`ENLIST_API_KEY` exists.
6. `Database-Design.md:110` — "defaults to `Environment.AgentName`"; no such API (it is `MachineName`).
7. `FRS.md:146,151` — a `/packages` page that does not exist (packages are per application).
8. `BRD.md:71` — "deleting … a machine that is still in active use is rejected"; agent delete is never blocked.
9. Test-project count wrong in three places (four vs the real five); sample service/job counts wrong in `Developer-Setup-Guide.md:90,93`.
10. `Deployment-IaC.md:73` quotes a startup error naming the **deleted** migration `20260908171028_InitialCreate` **[V]**; `:289` still says "agents authenticate to nothing — there is no auth layer at all" **[V]**.

**Self-contradictions:** `Authentication-Design.md:4` says C2 is closed then says it stays open **[V]**; `BRD.md:60` lists RBAC as out of scope then describes it; `Container-Story.md:747` says C5 "remains a proposal" 70 lines after describing it as shipped, and §12 sections run 12.5 → 12.7 → 12.8 → 12.9 → 12.6; `Demo-Install-Design.md:31,143,198` still argues from "enList has no authentication" and "not negotiable while C2 is open" **[V]**.

**Retired vocabulary** survives wholesale in `SRS.md §3.1-3.2` (Machine Registry, SRS-MCH-*, "Placement (Assignments)") and in `SRS.md:158-159`, which describes a master-detail portal that was never built.

**Missing** (most valuable first): no FRS functional area and no SRS requirements for *any* 2026-09-11 feature (enroll, revoke, API keys, sign-in and roles); the Developer Setup Guide never mentions that a `Required` portal needs `ControlPlane:ApiKey` via `Enlist.Portal.exe protect`, nor that an agent must be enrolled before its service definition is created; LLD has no section on the authorization algorithm; UI-UX documents neither the role-gating convention nor the new app-bar identity.

**Clean:** every relative link and heading anchor across all tracked markdown resolves; no mojibake; no TODO/FIXME anywhere in source **[V]**.

## 7. Build, config, packaging

- `Runner/Dockerfile` does not `COPY Directory.Build.props`, so **the runner inside the published image is version 1.0.0, not 3.0.0**; there is no `.dockerignore`, so local `bin/`+`obj/` are copied into the build stage.
- `RunnerProtocol.Version` is still 1 and its comment says a version skew "cannot arise yet" — but `enlist/runner` images now ship independently, and the `Success`→`Outcome` change went out without a bump, so an older image reports v1 and `"success": false` reads as Succeeded. Bump to 2 in both runners, one commit.
- `Runner.Legacy.csproj:11-13` claims its packages are invisible to plugins; on net472 `Assembly.LoadFrom` shares one AppDomain and the runner's System.Text.Json closure **is** visible to plugins — exactly the assemblies a net472 plugin ships at another version. `LegacyPluginLoadContext.cs:23-27` repeats the claim.
- `System.Reflection.MetadataLoadContext` is pinned at 9.0.9 on a net10 project whose other packages are 10.0.x.
- **[V]** No `.gitattributes`; the index is uniformly LF and `core.autocrlf=true` papers over it. One line would make it explicit.
- **[V]** `demo/demo-status.ps1` has no UTF-8 BOM; the other two scripts do. No non-ASCII bytes in any of the three.
- **[V]** `.gitignore` is correct: `.env`, `PackageBlobs/`, `.demo/`, `deploy/` generated content, `bin`/`obj`, `.vs/`, `.claude/settings.local.json` are all covered, and `deploy/README.md` stays tracked.
- **[V]** Test counts match the documented 232 exactly: Runner 50, Deploy 5, ControlPlane 72, Agent 72, Portal 33.

## 8. Ops docs, scripts and config

Read on the fourth attempt, after three rate-limit deaths. **43 findings across 18 files live in
their own document: [review-2026-09-12-ops.md](review-2026-09-12-ops.md)**, which keeps each
finding's quoted text and cited code lookup. Headlines only here.

**Instructions that cannot work as written.** Four documents show control planes and portals that
would refuse to start today under the two listener rules: `Deployment-IaC.md` §4.1 and §4.2 (the
aspnet image defaults to `http://+:8080`, and neither example sets a mode or a key),
`Installer-UI-Design.md:119,131,160,264,266` (`http://+:5293` and `http://+:5231` as page defaults
and in the silent-install example), and `Container-Developer-Guide.md:152`
(`--urls http://0.0.0.0:5293`). These are the lines someone copies.

**Runbook §3.13 cannot find what it is looking for.** Its duplicate-agent recipe matches
`enlist-agent.exe`, but §3.6 on the same page says the process name is literally `dotnet.exe` in the
development setup, which is the setup where duplicate agents actually happen. Both demo scripts
match `dotnet.exe` plus a command-line fragment for exactly this reason. §4, the escalation table,
also sits stranded in the middle of §3 with no rows for the authentication-era entries printed below
it.

**Two health checks probe the wrong endpoint.** `Runbook.md:10-16` and `demo/start-demo.ps1:121`
both use `GET /api/agents`, which is Viewer-policed and answers 401 under `Required`. `/health` was
built for this and is anonymous. The same mistake sits in `Installer-UI-Design.md:192`, whose
duplicate-name warning calls a Viewer endpoint while holding at most a join token.

**Script defects.** `start-demo.ps1` leaks five environment variables into the caller's shell and
never restores them, including `Authentication__Mode=Off` and a connection string containing the SA
password; it swallows the first-run 1.5 GB image pull behind `Out-Null`; and it leaves the control
plane running when its readiness check throws. `demo-db.ps1:333` prints the SA password to the
console on every `up`. `demo-status.ps1:78,91` uses the bare `2>$null` that `demo-db.ps1` documents
at length as a PowerShell 5.1 trap and routes every call through a wrapper to avoid.

**Config.** The `Dockerfile` never copies `Directory.Build.props`, so the runner inside the image is
version 1.0.0 while every host build is 3.0.0 — independently confirming §7. There is no
`.dockerignore`, so the documented build ships the whole repository root as context, including
`demo/sql-server/.env` and its SA password. The base `appsettings.json` carries a LocalDB connection
string that applies in Production, where a forgotten override fails as what looks like a network
error.

**Stale numbers, each a one-line edit:** "121 tests" twice, "800/800" once, "five tables / three
migrations" twice, and the migration-collapse narrative that predates two later collapses.

**Verified correct and deliberately not touched:** every numeric default in Deployment-IaC §1.3; the
authentication story across five documents and the code; all 17 projects in the solution file; test
package versions uniform across all five test projects; and the launch and appsettings defaults,
which sit on the safe side of both listener rules. `Application-Developer-Guide.md` had one wrong
line in 421. `deploy/README.md` was clean.

## 9. Coverage gaps in the suite

No test at any tier for: `POST /api/agents/{name}/commands` (the portal's Start/Stop buttons); `POST`/`GET /api/agents/{name}/logs`; a successful `DELETE /api/packages/{digest}`; an agent-tier route refusing *another agent's* token; expiry actually being enforced for join tokens or keys; the hub refusing a revoked agent on reconnect; the audit line for a *refused* write; a real net472 application end-to-end through AgentHost (the routing test registers the net10 runner under the net472 key).

Two skips can hide real failures: `PortalAuthenticationTests.cs:119` skips on **any** 400, including a genuine Negotiate regression; `PortalRolesTests` and one agent test return early off Windows instead of using `Skip.IfNot`, so they pass vacuously.

## 10. Suggested order of work

1. ~~§1 items 1, 7, 8 — the security-shaped ones.~~ **Done**, `a48da7a`.
2. ~~§1 items 2-4, 15-17 — the agent's logging, shutdown and refetch behaviour.~~ **Done**, `8e17af8`.
3. §5 — the ASCII sweep. Mechanical, and decide the markup question while there.
4. §2, §3 — comments and dead code. Nearly free once the code above settles.
5. §6 — docs, starting with the ten "wrong" items.
6. ~~§8 — actually read the ops docs and scripts.~~ **Done**, `97725d0`.

Still open in §1: items 10, 11, 13, 14, 19, 20 and 24. The runner and control-plane concurrency
family has been done; what is left is mostly input validation and reporting detail, worth reading
together rather than one at a time.
