# enList v3 — Architecture Design Review and Code Review

**Date:** 9 September 2026 (findings struck as they are fixed — see *Resolved* below)
**Scope:** 7 product projects (~16,400 lines), 5 test projects (~5,200 lines), 20 documents
**Method:** every product source file and every test read line by line; every claim that could be checked against the running demo fleet — agent logs, the control-plane database, the live portal — was checked rather than inferred.

### Resolved since the review

Finding IDs are stable and are what the Runbook, LLD and commit history refer to; a fixed finding is removed from the lists below and recorded here instead.

| IDs | Resolved by |
|---|---|
| C1, M12 | Both runners: concurrent service registry, Start/Stop serialized per service, guarded shutdown. Pinned by `ConcurrentCommandTests`; symptom recorded in [Runbook §3.14](../05-operations/Runbook.md). |
| H4, H5 | Agent: per-application lifecycle gates, concurrent registries, snapshot lock, crash-retries that re-read the current assignment; receive loops that survive a faulting handler. Pinned by `AgentConcurrencyTests`; model described in [LLD §8.1](../03-architecture/LLD.md). |
| H3, M5 | Portal: the edit dialog restates the mode on the isolation the rule already carries (`IsolationSpec.WithMode`) and shows what it keeps; the Agents tab calls the shared `PolicyConflictDetector.Agree`. Pinned by the new `Enlist.Portal.Tests` (`PolicyConflictDetectorTests`, `TagSelectorMatcherTests`) and by `IsolationSpecTests`. |
| H1 | Agent: a reconnect policy that never gives up, a `Closed` handler that reopens the connection, a 3-minute re-fetch floor under push, and the push channel's state reported with capabilities and shown on the Agents tab as *not receiving*. Pinned by `AgentReconnectTests` — a real control plane killed for 50 seconds and relaunched on the same address. |
| H2 | Control plane: `ReportRetentionSweepService` deletes status reports older than a day (configurable, batched), always keeping each agent's newest so a silent agent still shows its last known state. Pinned by `ReportRetentionTests`. |
| M1, M2, M7, M10 | Control plane: `PackageDigests` / `ApplicationNames` validators in Contracts, applied at every endpoint that takes either (and again in `PackageBlobStore`, `PackageCache` and `RunnerStaging`, the lines that turn them into paths); a rule naming a digest the control plane does not have is refused on create and update; the upload endpoint raises Kestrel's body limit to `PackageStorage:MaxUploadBytes` (512 MB). The portal's upload dialog and `enlist-deploy` apply the name rule before sending, and the CLI now reports a refusal in the server's words. Pinned by `ApiValidationTests` and a `PackageCacheTests` case. |
| M3, M4, M8, L7 | Agent: cron sleeps are sliced (a day at most per `Task.Delay`), so a yearly cron no longer faults its loop unobserved; a tick that arrives while the previous run is still in flight is skipped and written to the application's log rather than stacked; a run command that cannot be sent is logged as a lost firing; the loop reports a callback that throws; and the loop fires once per occurrence — a timer waking a fraction early used to fire the same occurrence two or three times (duplicate run commands, found live while verifying the overlap policy). One definition of the cron dialect (`CronExpressions` in Contracts — five fields or six, `?` for `*`) is applied by the scheduler, by the API (a bad override is a 400) and by the portal's cron fields. Pinned by `JobSchedulerTests`, `JobOverlapTests` (a ten-second job on a two-second cron, against a real runner) and `CronExpressionsTests`. |
| M11 | Runner tests: `LegacyRunnerTests` drives the real net472 `enlist-runner.exe` through the same `StubAgent` and protocol as the modern runner, against `deploy/LegacySample` — discovery and the protocol version, a service's start/output/stop, a job with the agent's settings, cancelling an in-flight batch at a chunk boundary, and exiting when the pipe drops. `StubAgent.StartLegacyAsync` is the only new seam. |
| M6, M9, M13, M14 | Control plane: the manifest is scanned once at upload and stored on the package row (`ManifestJson`; its migration `PackageManifestJson` has since been folded into `InitialCreate`), served from there, and back-filled on first request for older rows; the retention sweeps log what they delete and any failure. Agent: every `docker` command except `wait` is bounded (60 s, then the CLI is killed and a `TimeoutException` names the command); the status reporter and log forwarder say the first failure, one line per five minutes while it lasts, and the recovery (`FailureNotice`, via `IReportsDiagnostics` into the agent log), and a non-2xx now counts as a failure; the staging directory is removed on every start-failure path and on give-up. Pinned by `PackageManifestTests`, `DockerContainerEngineTests`, `FailureNoticeTests` and a `AgentStartupFailureTests` assertion. Still open from M13: no `ILogger` in the agent, so nothing reaches the Windows Event Log. |
| L1, L2, L3, L4, L5, L6, L8, L9 | The eight low items, all at once. Comments and contracts brought back into line (`DesiredStates` constants, the stacked and orphaned summaries, the mangled quotation, the stale 409 note). Both runners discover lifecycle methods declared on a base class, and both say — as a Warning in the plugin's own log — when a settings file exists but cannot be read. The wire channel drops a line past 1 MB and skips a line it cannot read as a message, reporting either instead of ending the connection. A filtered unique index on `(ApplicationName, VersionNumber)` plus a renumber-on-collision retry turns the version race into nothing. The portal fetches each agent's report once per two seconds per tab however many panels ask (`AgentReportCache`). And the miscellany: the forwarder's queue bound, the capability probe using the backend's own engine, the command literals, an invalid `--control-plane` URL refused with a message, the runner's `StoppingCts` disposed, the duplicated sentence. The non-zip upload noticed under M1–M10 is a 400 that leaves neither row nor blob. Pinned by `MessageChannelTests`, `AgentReportCacheTests`, two `RunnerLifecycleTests`, a `LegacyRunnerTests` assertion, and two control-plane tests. |
| H6, M15 | Found on 10 September while the second container engine was added, fixed the same day — see §4.1 and §4.2. H6: a container engine's port proxy accepts a control-channel connection before the runner inside is listening, so under load the backend's dial-in handed the agent a connection that closed before a byte and the start failed as a runner crash; `ContainerRunnerBackend.ConnectAsync` now dials again unless the far end speaks or stays open. M15: a runner whose agent vanished with a reset — what a dead agent looks like from behind that proxy — died of an unhandled `IOException` (exit 139, a stack trace, services stopped by nobody) where end-of-stream already meant "stop everything and leave"; both runners now treat the two alike. Pinned by `ContainerControlChannelTests` (no engine at all) and `ListenTransportTests` (the `--listen` transport, covered at runner level for the first time). |

H6 and M15 postdate the review: they were found on 10 September by the second container engine's own tests and are numbered here so that every finding lives in one list.

---

## 1. Verdict

**The architecture is sound and unusually well-reasoned.** The five decisions the system rests on — agents that only dial out, content-addressed packages, a plugin contract matched by name rather than type identity, one process per running application, and tags as the sole targeting mechanism — are each correct for the problem, each implemented as described, and each explained in the code at the point where it matters. The `IRunnerBackend` seam was proven by a second real implementation (containers) that touched none of the reconciliation, supervision or status code above it. The test posture — real processes, a real database, real Docker, nothing mocked at the seams — is the right one and has caught real defects.

**It cannot yet run outside a lab with its clients attached, and the one structural finding is half closed.** The absence of authentication is being closed in three steps ([Authentication-Design.md](../03-architecture/Authentication-Design.md)). The control plane's side landed on 2026-09-11: every request is authenticated by default, and authentication can be off only when every listener is on loopback — so an unauthenticated control plane reachable from a network is no longer a configuration that starts. The agent's side followed the same day; the portal does not authenticate people yet (step 3), so a deployment with a portal still runs with authentication off, which now means loopback only.

**The pattern behind most defects is the one the project's own documentation already names.** enList deliberately keeps several things in duplicate — two runner builds, a portal that mirrors the control plane's resolution logic, a status DTO on both sides of an opaque blob. `Container-Story.md` §12.9 says every defect from the last regression pass was "a change applied to one copy of something that had two." This review found a third copy of the agreement predicate that had already drifted; the copy is gone, and the structural fix in §3.5 is still worth making.

| Severity | Open |
|---|---|
| Critical | 1 |
| High | 0 |
| Medium | 0 |
| Low | 0 |

---

## 2. Findings, ranked

Severity reflects consequence in production, not effort to fix. Several of the worst are small changes. Each is expanded, with file and line, in §4.

| # | Sev. | Finding | Where |
|---|---|---|---|
| C2 | Critical | **No authentication or authorization on any surface** — anyone who can reach the control plane can upload code every agent will run, or join any agent's command group. **Steps 1 and 2 of 3 implemented 2026-09-11** (control plane: bearer scheme, policy table, credential tables, both hard rules, enrollment, CLI verbs; agent: join-token enrollment, the DPAPI credential file, the credential on every call and on the hub — [Authentication-Design.md](../03-architecture/Authentication-Design.md) §13); open until the portal step lands. | `ControlPlane/Program.cs` · `Hubs/ApplicationPolicyHub.cs:17` |

---

## 3. Architecture review

### 3.1 What holds up

Credit first, because it is earned and because it frames what follows: the defects below are almost all local, and none of them argues for changing the shape of the system.

- **Outbound-only agents.** Every agent-facing interaction is an agent calling in over REST or receiving a push on a hub connection it opened. `ListenAsync` in the runner is the one deliberate inversion, and it exists precisely so the control channel can be published to loopback only. The docs, code comments and tests all agree on this and enforce it.
- **Content addressing done properly.** The digest is computed server-side from received bytes (`PackageBlobStore.SaveAsync`), verified on download (`PackageCache.DownloadAndVerifyAsync`), and version numbers are monotonic rather than positional, so a garbage-collected v1 never turns v3 into v2. Retention is mark-and-sweep with a rollback window. This is a small, correct design.
- **Name-matched attributes with an enforced zero-dependency runner.** `EnforceZeroPackageReferences` in the runner's csproj turns the load-bearing constraint into a build error. `PluginDiscovery` reads `CustomAttributeData` only, never instantiating. The net472 runner shares no binary and still speaks the identical contract — proven live.
- **Tags-only targeting with one resolver.** Collapsing explicit targeting into an implicit `agent=<name>` self-tag removed an entire precedence problem. Agreement-based conflict resolution (equivalent rules coexist, disagreeing ones fail loudly with the rule IDs) is the right call over "most specific wins", and the port-collision check is a genuinely different conflict class handled as one. Both callers share `ResolveMatchingPoliciesForAgentAsync`, which the Runbook records was learned the hard way.
- **A seam proven by use.** `IRunnerBackend`/`IRunnerInstance` were extracted before containers existed and the container backend then landed with no branch in `AgentHost`. The §6.4 invariant ("AgentHost never branches on backend type") held under real pressure — `Cleanup` and `ReapOrphansAsync` went on the interface rather than becoming `if (isContainer)`. It held again on 10 September: `IContainerEngine` took a second implementation, `WslContainerEngine` over `wslc`, with no change above the seam — and the one defect the new engine exposed (H6) was in the backend's dial-in, the layer the seam says such things belong to.
- **Protocol versioning that has already earned its keep.** `RunnerProtocol` exists for a case that could not yet arise, and Container-Story §12.8 records it catching the legacy runner's drift within days.
- **Developer modes that reuse the real host.** `--dev` and `--check` drive the ordinary `RunnerHost` over a real pipe. There is no second execution path to diverge.
- **Production posture on the database.** Migrate in Development only; verify and refuse to start otherwise. The error messages name the fix.
- **Testing that spawns the real thing.** Real `enlist-runner` processes over real pipes and sockets, the real control plane against a throwaway LocalDB, real containers — with the LocalDB process-tree hazard understood and pinned.

### 3.2 Security boundary (C2)

> **Design decided 2026-09-11:** [Authentication-Design.md](../03-architecture/Authentication-Design.md) - Windows authentication for operators, join-token enrollment for agents, TLS at Kestrel, and authentication that can only be off on loopback. **Steps 1 and 2 implemented 2026-09-11** — the control plane (`AuthenticationTests`) and the agent (`AgentCredentialTests`); C2 stays open until the portal (step 3) follows.

*As found on 9 September.* The documents were consistent: no authentication anywhere, assumed to run inside a trusted network. What the documents do not spell out is what that assumption is carrying. Concretely, any host that can reach the control plane's port can:

- `POST /api/packages` an arbitrary zip and `POST /api/application-policies` with an empty selector — **every agent in the fleet will download it and execute it** as whatever account the agent runs under, typically `LocalSystem`.
- Join any agent's SignalR group by name (`ApplicationPolicyHub.JoinAgentGroup`, `ApplicationPolicyHub.cs:17`) and receive the commands meant for it; or, since `UpsertAgentSeenAsync` registers on first contact, impersonate an agent and post status reports.
- Delete agents, packages and rules; disable scheduling fleet-wide.

The runner's own control channel is also unauthenticated and the code says so in three places; loopback-only publishing is what confines it, which is correct for the process and container backends as built. The control plane has no equivalent confinement — `AllowedHosts: "*"`, no HTTPS redirection (the portal has both `UseHttpsRedirection` and HSTS; the control plane has neither).

*Since the review (step 1, 2026-09-11).* Under `Authentication:Mode=Required` — the default — the three bullets above are closed on the control plane's side: `POST /api/packages` and every policy, tag, scheduling and command write need a management key with the Operator role; `JoinAgentGroup` compares the requested name to the `enlist:agent` claim on the connection and refuses any other; reports, logs and capabilities are accepted only from the credential bound to that name; deletes are Operator. The confinement the control plane lacked is a startup rule rather than a redirect: plain HTTP off loopback refuses to start under `Required`, and `Off` refuses to start off loopback, with no override. The agent's end followed the same day (step 2): it enrolls once with a join token, keeps its credential DPAPI-protected under a closed ACL, presents it on every call and on the hub, and treats a rejection as a loud, rate-limited log line rather than a reason to stop anything. What is not yet built is the portal authenticating people and holding its own key (step 3), so a deployment with a portal still runs `Off`, on loopback. Pinned by `AuthenticationTests` and `AgentCredentialTests`.

> **This was a design decision to make, not a patch to apply** — made on 2026-09-11, in the shape this paragraph anticipated: a per-agent bearer credential issued at registration and required on every agent endpoint and on hub connect (binding `JoinAgentGroup` to the authenticated identity); a separate operator credential for the portal and the mutation endpoints; and TLS on the control plane. The "trusted network boundary" line is not needed in the Deployment guide after all: since step 1 the assumption cannot be configured, and [Deployment-IaC §1.8](../05-operations/Deployment-IaC.md) carries the rules instead.

### 3.3 Liveness and resilience (H1 — resolved)

The agent's health signal is `LastSeenUtc`, updated by every policy fetch and status POST, backed by a two-minute heartbeat against a five-minute staleness threshold. That answers "is the agent process alive"; it said nothing about whether the agent could still *receive*, and the two diverged: `ControlPlaneAssignmentSource` used SignalR's stock reconnect policy (four attempts, about 42 seconds) with no `Closed` handler, so an agent that outlived a longer control-plane outage — a migration, a slow deploy, a host reboot — stopped reconciling for the rest of its life while still heartbeating over HTTP and reading as Online.

Resolved: the reconnect policy never gives up (ramping to a 30-second interval), a `Closed` connection is reopened by the agent itself, `WaitForChangeAsync` returns every 3 minutes as a floor under push, and the connection's state travels with capabilities (`AgentCapabilitiesDto.PushChannel`) so the Agents tab shows *not receiving* beside Online. `AgentReconnectTests` restarts a real control plane after a 50-second outage and proves the next change still arrives. SAD §5 and HLD §2.2 now describe the outage branch.

### 3.4 Data lifecycle (M6 — resolved)

**Status reports (H2 — resolved).** Every `WriteStatusSnapshotAsync` — every state change, every job result, every reconcile pass, every two-minute heartbeat, from every agent — inserts a new `AgentReportEntity` row carrying the full JSON snapshot, and until this review nothing removed them: the demo database held 742 rows and 4.7 MB after 6.6 hours from two agents, about 8.6 MB per agent per day and roughly 60 GB a year for twenty, all of it read most-recent-first only. `ReportRetentionSweepService` now deletes reports older than a day (configurable) in batches, always keeping each agent's newest so an agent silent for longer than the window still shows its last known state; `ReportRetentionTests` pins both halves.


**Manifests (M6 — resolved).** The design doc's `Manifests` table was skipped in favour of discovering live (SAD §9), which was right for the runner. `GET /api/packages/{digest}/manifest` used to extract the zip and run `MetadataLoadContext` over every DLL on *every call*, and the Applications page called it once per digest on every refresh. The manifest is a pure function of immutable bytes, so it is now computed once at upload — beside the flavor, in the same handler — stored as `PackageEntity.ManifestJson`, and served from the row; the design doc's table arrived as a column.

**Job history.** Only the last run is kept, inside the snapshot blob. The Cancelled/Failed/Succeeded distinction added recently therefore has nowhere durable to go, and "did this job run last night, and how did it end" is unanswerable once the next run happens. This is the design doc's `JobRuns` table and it remains the most-asked-for thing the system cannot answer.

**The opaque blob is being read.** `AgentReportEntity.SnapshotJson` was stored unparsed so the agent's shape could evolve without a control-plane migration. The endpoint feed now deserializes it (tolerantly, and with a comment explaining why that is a weaker coupling than columns), and the portal mirrors its shape in `AgentStatusSnapshotDto`. That is fine — but the decision has now been made three times in three places, and it should be stated once as "the snapshot is a versioned contract read by two consumers" rather than as opaque.

### 3.5 The duplication tax (M11 — resolved)

The project keeps three things in deliberate duplicate, each with a written justification: the net472 runner (no shared assembly, by contract); the portal's `TagSelectorMatcher` and `PolicyConflictDetector` (client-side resolution without a round trip per agent); and the status snapshot DTO on both sides of the blob. The cost is stated plainly in Container-Story §12.9 — a fix lands on one side and nothing tells you — and the two-runner case now has a discipline (each names the other; both change in one commit).

The portal case has no such discipline, and it had drifted: a private copy of the agreement predicate on the Agents tab compared everything but Isolation, so two rules differing only in process-versus-container were flagged by the agent and by the Applications tab and not by the Agents tab (M5 — the copy is deleted, and `PolicyConflictDetector` now has direct tests).

The structural fix is available cheaply. Both the portal and the control plane already reference `Enlist.ControlPlane.Contracts`. `SelectorMatches`, `EffectiveAgentTags`, the agreement test and the port-collision check are pure functions over DTO shapes; moving them into that assembly gives the control plane and the portal one implementation each instead of a mirror to keep in step by inspection. The stated reason for the mirror — no server round trip — is unaffected, since the portal would still evaluate locally.

The legacy runner's copy is well-managed at the source level (a drift analysis for this review found every difference to be a forced net472 substitution: `Substring` for ranges, `IDisposable` for `IAsyncDisposable`, no socket transports). What it lacked was a test: nothing launched `Enlist.Runner.Legacy`'s executable, and every legacy-specific defect so far — §12.8's version drift, §12.9's swallowed load failures, a dispatch race that reached the net472 runner unnoticed until this review — lived in that untested half. `LegacyRunnerTests` now drives the real `enlist-runner.exe` through the same `StubAgent` as the modern runner, against `deploy/LegacySample`: discovery and the protocol version, lifecycle, a job with settings, cancellation at a chunk boundary, exit on a dropped pipe. The next change to either runner has a check on the other.

### 3.6 Scheduling semantics (M3, M4, M8, L7 — resolved)

The scheduler is deliberately minimal — Cronos for the arithmetic, one loop per registration — and that is the right size. What was missing was semantics rather than code, and all four gaps are closed: a run still in flight when its next tick comes is skipped and written to the application's log, never stacked (`JobTrackedState.InFlightRuns`, cleared by the result, by a Stop, or by the application restarting); sleeps are sliced at a day so a yearly cron cannot exceed what `Task.Delay` accepts, and a loop that stops for any other reason says so in the agent log; a run command that cannot be delivered is logged as a lost firing; and the cron dialect is one definition (`CronExpressions`) that accepts both five- and six-field forms, is enforced by the API before an override is stored, and turns the portal's field red before Save. `JobOverlapTests` runs a ten-second job on a two-second cron against a real runner; `JobSchedulerTests` proves a delay longer than one slice still fires exactly once, on time.

### 3.7 Open questions

The design doc's §12 list has one item answered (in-flight pause: no). The remaining two are still real, and three more have appeared:

- **Reconciliation versus imperative control at small fleet sizes** — still unanswered, and the imperative layer has since grown (per-service start/stop, per-job enable/disable, agent exclusion). The two layers do not remember each other: a service stopped by command comes back on the next reconcile restart, a job disabled by command comes back after a crash. That is documented in the code as a first-cut trade; it will surprise operators.
- **Plugins that own a listening socket** — unchanged.
- **What "Online" means** — answered with H1: liveness of the process (`LastSeenUtc`) and of the push channel (`PushChannel`) are separate signals now, and the portal shows both.
- **Where the security boundary is** — decided (C2): at the control plane's listener, with a credential per agent and a Windows identity per operator. Built on the control plane and the agent; the portal follows.
- **Linux agents.** The agent is Windows-first (`JobObject`, named pipes, LocalDB in dev). The socket transport and the container backend already work without any of those. The gap for a Linux agent is the Job Object's guarantee and Windows Service hosting; both are replaceable (systemd, cgroups). Worth deciding whether this is a target before the runner image ships anywhere.

---

## 4. Code review by component

Findings are grouped by the project that owns the fix. Line numbers are as of the review date and drift as files change; the finding IDs do not.

### 4.1 Runner and legacy runner

*Notes on the dev tooling.* `DevConsole`, `LogWindow` and `CheckReport` are clean, degrade correctly under redirection, and are tested through `IConsoleSurface`. `LogWindow.TryStart`'s synchronous 10-second wait is acceptable for a dev tool. Nothing to change.

*Since the review (M15).* `RunnerHost.RunAsync` treated a closed channel as a shutdown but a failed one as fatal: a reset from a vanished agent escaped as an unhandled `IOException`, which the Job Object hid on Windows (the runner dies with its agent regardless) and a container did not — exit 139, a stack trace in the container log, services stopped by nobody. Both runners now leave the receive loop on `IOException` exactly as on end-of-stream. `ListenTransportTests` drives `--listen` the way the container backend does, minus the engine, and resets the connection.

### 4.2 Agent

*What is good here and should not be touched:* `PackageCache`'s marker-file protocol and digest verification; the pruning-after-reconcile ordering and its documented file-lock race; `CrashBackoff`'s settle reset; `FireAndForget`'s tracking so `StopAsync` can drain retries; the `DisposeAsync` ordering (kill, then drain) in both instances and the reasoning written beside it; orphan reaping scoped by ownership label rather than name prefix.

*Since the review: the second engine.* `WslContainerEngine` drives `wslc` (WSL 2.9.11 and later) behind the same `IContainerEngine`, chosen by `--container-engine`, reported with capabilities and shown on the Agents tab. It absorbs four verified differences — publishing binds to loopback by default (right for the control port, so application ports get an explicit `0.0.0.0:`), host port 0 is rejected (a dynamic LAN port is bound and released locally, a small race the start fails loudly on), no `port` verb (read from `inspect`), no `wait` verb (exit polled once a second, so a crash is seen up to a second later) — and shares `CliContainerEngineBase` with Docker for the bounded, killed-on-expiry command runner. What the engine exposed is H6, and it was fixed where it belongs: a port proxy that accepts before the runner listens is a property of proxies, not of `wslc`, so the settle in `ContainerRunnerBackend.ConnectAsync` is engine-neutral and costs a container start at most a second. One thing to watch: when `wslc stop` refuses a container that has already exited, `StopAsync` reads the exit code the container already has and judges 137/143 as a kill — the same reading as Docker's, in a place Docker never needs it.
*Since the review (C2 step 2).* `AgentEnrollment` settles the credential before anything else is built — probe the stored one, enroll with a join token, or ask `/health` whether one is needed — and every outcome, a refusal to start included, is written to the agent log, because the SCM shows nothing. `AgentCredentialStore` creates the file with its final ACL in the same call, so there is no instant at which it is readable by whoever can read the data root. One `AgentCredential` feeds a handler per `HttpClient` and the hub's access-token provider, and says a rejection once through the assignment source's diagnostics. One thing to watch: the startup probe is a policy fetch, which bumps `LastSeenUtc`, so an agent that then refuses to start has been "seen" once — harmless, and visible in the registry as an agent that appeared and went quiet.

### 4.3 Control plane and contracts

*Also worth stating:* resolution loads every rule per agent fetch (`Program.cs:756`) and every agent fetches on every push. Linear in rules × agents, entirely fine at the scale the design doc targets; note it for the day a fleet has thousands of rules. `GetEndpointsAsync` runs one query per agent (N+1) for the same reason and the same verdict.
*Since the review (C2 step 1).* `EnlistBearerHandler` validates the three token kinds by hash and emits the claims; `EndpointPolicies.Table` is the code twin of the design's §7 table and its handler fails closed — an endpoint missing from the table is refused to everyone, which is deliberately the review comment for any future endpoint; `ListenerRules` enforce both hard rules before Kestrel binds; six verbs on the executable run against the database, so the first Operator needs no HTTP credential. Two things to watch: `LastUsedAtUtc` is written at most once a minute per credential, a write on the request path that is cheap now and worth remembering if it ever is not; and under `Off` a credential that is presented is still validated, and a revoked one is logged on every request — harmless on loopback, but `Off` means "not required", not "ignored".

### 4.4 Portal

*Good:* the dialogs are honest about what they know and when ("read once on open and does not refresh — close and reopen"); the toggler names the blast radius and says "and future"; the wizard previews conflicts and port collisions before saving and still lets the operator proceed; the upload dialog scans the uploaded package and removes an empty one immediately. The portal has `UseHttpsRedirection`, HSTS and antiforgery; the control plane, since C2 step 1, refuses plain HTTP off loopback outright rather than redirecting it, which is the stronger of the two. Portal tests cover the resolution logic only — see §5.

### 4.5 Deploy, samples, contract

`enlist-deploy` does one thing, checks the application name before it sends, and reports a refusal in the server's own words (a non-2xx used to surface as a bare `EnsureSuccessStatusCode` stack trace). The contract file is correct and its header explains itself. The samples are good teaching material; every service returns from `Start` promptly and observes its token, and Ledger Rebuild / Legacy Batch demonstrate cancellation at a safe boundary. `Enlist.Contracts.props`'s source-only inclusion is exactly right.

---

## 5. Tests

211 tests across five projects, all passing (148 at the time of the review). The posture is the project's strongest engineering asset: nothing at a process, database or engine boundary is faked. Coverage is strongest where the design is most novel — transports, the backend seam, containers, package distribution, conflict resolution, and now concurrent dispatch and the host's colliding callers — and thinnest where the remaining findings sit.

| Area | Covered | Gap |
|---|---|---|
| Runner lifecycle | Discovery, start with console capture, job settings merge, pipe/socket close, cancellation, shutdown drain, read-loop non-blocking, concurrent starts, per-service ordering, the `--listen` transport, exit 0 on a connection reset | — |
| Legacy runner | Flavor detection from a net472 zip (Deploy tests); runner-bin *selection* by flavor name; the real net472 binary through `StubAgent` — discovery, protocol version, lifecycle, a job with settings, cancellation, exit on a dropped pipe | A multi-service net472 sample, to mirror `ConcurrentCommandTests` on that runtime. |
| Agent supervision | Crash restart, backoff cap, connect timeout, sibling isolation, Job Object, container restart, orphan reaping, port publishing, a faulting message handler, retries colliding with reconciles, a control-plane outage longer than the stock reconnect window, convergence with no push channel at all, a hung engine command cut short, staging cleaned up on give-up, rate-limited failure notices, a port proxy that accepts before the runner listens (no engine), the same container lifecycle on real `wslc`, an agent enrolling with a join token and running as itself, a revoked credential leaving its applications alone | — |
| Scheduler | Re-registration, unregister-all, sliced sleeps that still fire once on time, a callback that throws, a ten-second job on a two-second cron (skipped, not stacked), both cron dialects | A send failure at fire time (M8) — the path is a try/catch and a log line, but forcing it needs a fake runner instance the test seams do not offer yet. |
| Control plane API | Packages, registry, self-tag, agreement/conflict, exclusion, package and report retention, flavor/isolation rules, ports, endpoint feed, digest and name validation, unknown-digest refusal, uploads over the stock body limit; since C2 step 1: anonymous reaches `/health` only, join-token enrollment and its name binding, roles on keys, hub group binding, both startup rules | — |
| Portal | `PolicyConflictDetector`, `TagSelectorMatcher` and `IsolationSpec.WithMode`, as plain xUnit | No component tests; dialogs, tables and the wizard are verified by hand. |

One observation on the suite itself: the first full run after killing demo processes produced three failures, one per assembly, that did not recur across subsequent runs. Consistent with test servers racing freed ports. Recorded since in [Test-Plan §2a](../05-operations/Test-Plan.md).

---

## 6. Documentation

The documents were audited line-by-line in the previous session and are accurate to the code in almost every claim checked here. The corrections this review implies:

- **LLD §3**'s portal snippet omits the self-tag that the real `TagSelectorMatcher` handles (`LLD.md:271-272`).
- **Application-Developer-Guide** should state: base-class lifecycle methods are not discovered; imperative service/job state does not survive a restart. (The cron dialect and the overlap policy are documented there now.)
- ~~**Deployment-IaC** should lead with the trusted-network assumption and the consequence in C2.~~ Overtaken on 2026-09-11: since C2 step 1 the assumption is not configurable, and §1.8 of that document carries the rules instead.
- ~~**BRD §5.2** "Out of scope" is accurate; C2 argues it should move to "Required before production" with a one-line design.~~ Done 2026-09-11: BRD §5.2 and §7, SRS and SAD now say what is built and what is not.

---

## 7. Recommended order

Sequenced by consequence per hour.

1. **C2** — ~~decide the authentication model~~ decided 2026-09-11 ([Authentication-Design.md](../03-architecture/Authentication-Design.md)) and built on the control plane and the agent. Next: step 3 — Windows authentication on the portal, the portal's key, `--api-key` on the CLI, TLS at Kestrel. Everything above it can ship without it; nothing should ship past loopback without it.

Nothing in this list changes the architecture. The system's shape is right; the trust boundary is decided and built on two of its three sides — the remaining work is the portal.
