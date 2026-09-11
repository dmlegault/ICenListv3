# Software Test Plan & Test Suite

**Product:** enList v3
**Framework:** xUnit
**Document status:** Derived from the actual test source under `tests/`; rewritten 2026-09-08 at class level after the per-test list drifted. Total: **205 tests, all passing** (`dotnet test enList_v3.slnx`).

---

## 1. Test Strategy

- **No mocking of the control plane's own database or HTTP surface for integration-level tests.** `Enlist.ControlPlane.Tests` and the control-plane-backed parts of `Enlist.Agent.Tests`/`Enlist.Deploy.Tests` run against a real, ephemeral `ControlPlaneTestServer` (`tests/Enlist.TestSupport/ControlPlaneTestServer.cs`) — a real ASP.NET Core test host with a real (throwaway) database — rather than stubbing EF Core or the HTTP client. This is a deliberate choice consistent with this project's general posture (see [`Runbook.md`](Runbook.md) for an incident this posture was specifically meant to catch: a mocked-away integration bug that a real test host exposes).
- **The Runner is tested against a real named-pipe server**, `StubAgent` (`tests/Enlist.Runner.Tests/StubAgent.cs`) — a minimal stand-in for `Enlist.Agent` that speaks the exact same wire protocol, so runner tests exercise the real `MessageChannel`/pipe transport, not an in-process fake.
- **Shared test infrastructure** lives in `tests/Enlist.TestSupport/`: `ControlPlaneTestServer` (with `StopAsync`/`StartAgainAsync` to bring the same control plane back on the same address for outage tests), `RepoPaths` (locates repo-relative paths like `samples/` and `deploy/` from a test's own working directory), `TestPackaging` (zips a sample app's build output for upload tests).

## 2. Test Projects and Coverage

Listed by test CLASS rather than by individual test. The per-test enumeration this section used to carry drifted badly — it described 51 tests under the pre-rename vocabulary (machines, assignments, labels) long after those concepts were gone — and a class-level inventory stays true for longer while still saying what is actually covered.

### 2.1 `Enlist.ControlPlane.Tests` — 68 tests

Spawns the **real** `Enlist.ControlPlane.exe` against a throwaway LocalDB database per test class (`ControlPlaneTestServer`), so these exercise routing, EF Core, validation and JSON exactly as deployed.

| Class | Verifies |
|---|---|
| `ControlPlanePackageTests` | Content-addressed upload/download, digest correctness, idempotent re-upload, 404 for unknown digests, and eight concurrent uploads for one application numbered 1–8 with no duplicate. |
| `AgentRegistryTests` | Implicit registration on first contact, pre-registration by setting tags, tag-selector resolution, agent scheduling exclusion. |
| `PackageRetentionTests` | Mark-and-sweep retention: an unreferenced package is eventually deleted; a referenced one never is. |
| `ReportRetentionTests` | Status-report retention on a seconds timescale against the real database: reports older than the window are swept once a newer one exists and the newest keeps its content; an agent's only report is never deleted however old it is — the exemption that stops a silent agent vanishing from the portal. |
| `ApiValidationTests` | What the API refuses at the door: a digest that is not 64 hex characters is a 400 on every package endpoint before it can become a path (a well-formed unknown one is still a 404); an application name that could be a path or a device name is a 400 on upload and on rule creation; a rule naming a digest the control plane does not have is a 400 on create and on update (a real one is accepted, and stored lowercase however it was written); an upload larger than Kestrel's stock 30 MB limit succeeds; a body that is not a zip is a 400 that leaves neither a row nor a blob. |
| `HealthEndpointTests` | `GET /health` against the real control plane and database: a healthy control plane answers 200 naming the product, a version without build metadata, and the newest applied migration (checked against `__EFMigrationsHistory` itself); taking the database offline after startup turns that into a 503 that still names the product and gives a reason that is not the connection string, and it is 200 again the moment the database is back — no restart. |
| `AuthenticationTests` | The control plane under `Authentication:Mode=Required`, driven as an operator and an agent would drive it (the CLI verbs mint the credentials; HTTP and the real SignalR client present them): nothing but `/health` answers an anonymous caller, and `/health` says the mode; a join token enrolls an agent once, the credential it yields works on that agent's own routes, is 403 on any other agent's, cannot upload packages or read the registry, and may download packages; a name already holding a credential is a 409 until revoked, after which the old token is 401 and the name re-enrolls; keys carry roles (Viewer reads, Operator writes) and deleting an agent revokes its credential; the hub admits an agent to its own group only and refuses an anonymous connection at negotiate; `Off` off loopback and `Required` on plain HTTP off loopback both refuse to start; and `Off` on loopback is anonymous exactly as before. |
| `CronExpressionsTests` | The cron dialect enList accepts, pinned where it is defined: the five-field and six-field forms of one schedule agree on the next occurrence; `?` reads as `*`; the wrong field count and an out-of-range value are refused with the reason. |
| `PackageManifestTests` | The manifest is scanned once at upload and stored on the package row, served from there, and — for a row from before the column existed — scanned on first request and kept. |
| `RuntimeFlavorTests` | Flavor validation and defaulting for `Path`-based rules, and rejection of unrecognized values. |
| `FlavorIsolationTests` | A rule inherits its package's DETECTED flavor; a contradicting claim is rejected; net472 + container is rejected on create AND on an update that only changes the package; a `Path`-based rule keeps its flavor across unrelated updates. |
| `IsolationFieldTests` | Port range and protocol validation; image, env, networks and ports round-trip. |
| `IsolationSpecTests` | The contract type on its own, no server. `AgreesWith` compares ports by value where record equality compares the list instance (the phantom-conflict trap) and sees a changed host port; `WithMode` keeps every container detail when the mode is restated as container and drops them on a move to process — the guard behind the edit dialog no longer saving a container rule without its ports. |
| `PortCollisionTests` | Two applications claiming one static host port on one agent both fail, each naming the other — plus the three cases that must NOT be conflicts (dynamic ports, same port on different agents, different host ports sharing a container port). |
| `EndpointFeedTests` | The endpoint projection, staleness, the Traefik shape (and that it emits no `routers`), and that an unreadable snapshot degrades instead of breaking the feed. |

### 2.2 `Enlist.Agent.Tests` — 66 tests

Spawns real runner processes, and (where Docker is present) real containers.

| Class | Verifies |
|---|---|
| `AgentHostTests` | Start/stop lifecycle, status snapshot shape, service and job command dispatch, and that a job stopped by an operator is recorded as Cancelled rather than Failed — in the snapshot the portal reads AND in the log line an operator reads. |
| `AgentConcurrencyTests` | What has to stay true when the host's independent callers collide, against real runner processes with a stretched backoff so the collision is deterministic: a handler throwing on one message does not stop the agent hearing that runner (a later heartbeat still arrives, and the fault is in the agent log); a crash-retry stands down when a reconcile already restarted the application during its backoff (one process, still `Running`, no spurious "failed to start"); and a crash-retry does not resurrect an application disabled during its backoff. |
| `JobOverlapTests` | A job slower than its schedule, against a real runner: OrderProcessor's ten-second Ledger Rebuild on a two-second cron starts once, and the ticks that arrive mid-run are skipped and written to the application's log — never stacked. |
| `DockerContainerEngineTests` / `FailureNoticeTests` | A stand-in `docker` that never answers is killed after the engine's timeout: the probe reports it, every other command throws a `TimeoutException` naming the command — no real engine needed. `FailureNotice`: the first failure is said at once, the next inside the window are not, one after the window carries the count, and a recovery is said once. |
| `AgentReconnectTests` | Against a real control plane killed and relaunched on the same address (`ControlPlaneTestServer.StopAsync`/`StartAgainAsync`): an agent reports its push channel as reconnecting, survives a 50-second outage — longer than SignalR's stock four attempts — and receives the next policy change by push with the re-fetch floor parked an hour away; and an agent with no push channel at all still converges on the re-fetch floor and reports the channel as disconnected. |
| `AgentCrashRestartTests` | A killed runner is restarted; repeated crashes back off and eventually give up. |
| `AgentStartupFailureTests` | A runner that never connects is retried and eventually abandoned — with its staging directory removed — without blocking sibling applications. |
| `AgentJobObjectTests` | An abnormally-dead agent takes its child runners with it (Windows Job Object). |
| `ControlPlaneReconciliationTests` | Live reconciliation: newly-matching rules start, removed ones stop, changed ones restart. |
| `PackageDistributionTests` / `PackageCacheTests` | Digest download, extraction, caching, restart-on-digest-change, and a malformed digest refused before it can become a cache path. |
| `RuntimeFlavorRoutingTests` | An application naming an unconfigured flavor is `Failed` and never started; the correct runner-bin is used per flavor. |
| `JobSchedulerTests` / `AgentFileLogSinkTests` | Cron registration and firing; re-registration replaces rather than duplicates; a delay longer than one sleep slice is slept in pieces and still fires exactly once, on time; a yearly cron does not fault its loop; a due-callback that throws is reported and the job is tried again next time. Log file writing and forwarding. |
| `ProcessRunnerBackendTransportTests` | The production `ProcessRunnerBackend` over BOTH named pipe and Unix domain socket. |
| `ContainerRunnerBackendTests` | An unmodified package runs containerized; discovery, identity (`RuntimeId`, `Pid = 0`), commands, graceful stop. |
| `ContainerControlChannelTests` | The backend's dial-in against an engine port proxy that accepts before anything inside is listening, with no engine at all: the test listens on the port a stand-in engine hands back, plays the proxy (accept, close) for the first connects and the runner after. A connection closed before the runner speaks is dialled again and discovery completes; a proxy that never finds a listener fails the start with a `TimeoutException` and the container is removed. |
| `ContainerCrashRestartTests` | `AgentHost` — given only a different backend — restarts a container after `docker kill`. The standing check on the §6.4 invariant. |
| `ContainerOrphanReapTests` | A container abandoned by a killed agent is reaped at next startup, AND one agent's reap leaves another agent's live containers alone. |
| `ContainerPortPublishingTests` | Static and dynamic ports published and reported as resolved endpoints; env and image overrides reach the ENGINE, not merely the API. |
| `WslContainerEngineTests` | The second engine, over the real `wslc` CLI and the `enlist/runner:wslc` image: an unmodified package runs and completes discovery with its control port on loopback only; application ports land on all interfaces, static and dynamic, and are reported; a killed container is restarted by the same crash path a process uses; orphans are reaped by ownership label and another agent's are left alone. Skips without `wslc` or the image. |

### 2.3 `Enlist.Runner.Tests` — 50 tests

Drives a real `enlist-runner` process through `StubAgent`, with no in-process shortcuts — both builds: the modern one as `dotnet enlist-runner.dll`, the net472 one as its own `enlist-runner.exe` (`StubAgent.StartLegacyAsync`, located by path since a net472 executable cannot be referenced from a net10.0 project).

| Class | Verifies |
|---|---|
| `RunnerLifecycleTests` | Attribute-based discovery — including lifecycle methods declared on a base class — service start with console capture, job execution with settings merge, a malformed settings file said as a Warning in the plugin's own log while the run still happens, and exit when the channel closes. |
| `SocketTransportTests` | The identical lifecycle over a Unix domain socket, plus the reported protocol version. |
| `ListenTransportTests` | The `--listen` transport, driven as the container backend drives it but with no engine: discovery completes over a listened port; and a connection *reset* without a shutdown command — a dead agent, as the runner sees it from behind an engine's port proxy — makes the runner stop its services and exit 0 rather than die of an unhandled `IOException`. |
| `MessageChannelTests` | The wire on its own, no process: a line with a kind this side does not know is skipped and reported and the next message still arrives; a line past the 1 MB limit is dropped without being kept; messages arriving in one read or split across reads come out the same. |
| `LegacyRunnerTests` | The real net472 runner against the real net472 sample, through the same stub and protocol as everything above: discovery reports the current protocol version and honest isolation zeros; a service starts, logs from .NET Framework, and stops; a job receives the agent's settings and succeeds; an in-flight batch is cancelled at a chunk boundary and reported Cancelled within seconds; a dropped pipe makes the runner exit. Until this class existed nothing ran that binary. |
| `ProtocolVersionTests` | A `ready` message with no version field deserializes as *unreported*, not as current — and mismatch is diagnosed distinctly from absence. |
| `LoadFailureWarningTests` | An unloadable assembly is NAMED in the warnings, the good half of the application still loads, and a clean application emits no warnings at all. |
| `CheckModeTests` | `--check`'s exit codes, which [`Application-Developer-Guide.md` §3](../02-building-applications/Application-Developer-Guide.md) documents as a build gate: `0` names the discovered services and jobs, `1` says what to look at when nothing was found, a relative `--app` path still resolves (the regression below), and `--check --dev` together is rejected rather than silently preferring one. |
| `JobCancellationTests` | Stopping one in-flight run against a real runner: a cancel reaches the job (proved by the plugin’s own log line, not the runner’s claim) and the run reports JobOutcome.Cancelled — its own outcome, never Failed; shutdown cancels a run in flight and drains it instead of exiting out from under it; a cancel with nothing running is informational; an unknown job faults. Plus a regression guard that a long SYNCHRONOUS job no longer blocks the runner's command read loop. |
| `ConcurrentCommandTests` | Commands arriving faster than the runner acts on them, which is the normal case, not an edge. Starting every service of a multi-service sample at once starts every one of them — the regression guard for a dispatch race that corrupted the runner's service registry and left the losing service parked at `Starting`, never invoked and never retried. And Start-then-Stop for ONE service is applied in arrival order even though each command runs on its own task. |
| `DevConsoleTests` | The dev host's console, through an `IConsoleSurface` fake. **Line editor:** a log line arriving mid-command preserves and repaints the partial input; cursor-relative insert/delete; history walking and its two boundaries; blank submissions not entering history; arrow keys at the edges NOT falling through and inserting a NUL; a redirected console reporting itself non-interactive so writes go straight out. **`--log-window` routing:** routed output reaches the window and leaves the typed command untouched; `WriteLocal` stays in the command window so it is never left blank; a closed window returns output here with a warning rather than dropping it silently. |

> The relative-path case is a real defect this suite now pins. A relative `--app` made the assembly load context throw for *every* plugin DLL, so discovery returned nothing — and the symptom is indistinguishable from an application whose attributes are wrong. The agent always passes an absolute path, so it survived until `--check` was pointed at a relative directory from a shell. Both runners now normalise with `Path.GetFullPath`.

> `Console.ReadKey` and cursor positioning both need a real console buffer no test host has, which is why the editor is tested through a surface interface rather than a terminal. That leaves one genuinely unverified seam — how a real Windows Terminal renders the repaints — but the editing arithmetic underneath it, where the silent bugs live, is covered.

**Not covered:** the interactive `--dev` session end to end. Nothing here drives real keystrokes, so the host's own behaviour was verified manually against both runners (services auto-start, a job runs on command, `[EnlistStop]` output is delivered on `quit`, exit code 0), and the redirected path is exercised by hand on every change because it is what scripted sessions depend on. The parts that could regress silently — discovery, settings merge, start/stop/execute — are the same code paths `RunnerLifecycleTests` already covers through `StubAgent`, because the dev host deliberately has no private entry point into `RunnerHost`.

### 2.4 `Enlist.Deploy.Tests` — 3 tests

| Class | Verifies |
|---|---|
| `DeployCliTests` | Zipping a source directory, uploading it, and printing the resulting digest and detected runtime flavor. |

### 2.5 `Enlist.Portal.Tests` — 18 tests

Plain xUnit over the portal assembly — no bUnit, no browser. What it pins are the portal's own copies of decisions the control plane makes, which is exactly the code that drifts silently: the review of 2026-09-09 found a private copy of the agreement rule on the Agents tab that had never learned about Isolation.

| Class | Verifies |
|---|---|
| `PolicyConflictDetectorTests` | `Agree` mirrors resolution field for field: identical rules agree, a missing isolation block equals an explicit process one, equal port lists in different list instances agree, and a difference in isolation mode, host port or a cron override disagrees. `FindConflictsByAgent` names only agents matched by disagreeing rules; `FindPortCollisions` reports a static host port another application holds on the same agent, ignores dynamic ports, and never counts an application against its own rules. |
| `TagSelectorMatcherTests` | The implicit `agent` self-tag matches a rule that names the agent; an empty selector matches every agent; every pair has to match; only the bare self-tag counts as "this agent only"; `DescribeScope` produces the three phrasings every screen shows. |
| `AgentReportCacheTests` | The fan-out limiter behind every polling panel: callers inside the window share one fetch, a caller after it gets a fresh one, different agents are fetched separately. |

Component rendering and interaction stay manual (§5).

### 2.6 Tests that skip themselves

The 16 container tests call `Skip.IfNot(...)` when their engine or its image is unavailable — Docker and `enlist/runner:dev` for the four Docker classes, `wslc` and `enlist/runner:wslc` for `WslContainerEngineTests` — and report as **skipped** rather than failed. The suite must stay runnable on a machine with no container engine, where a red test would say "enList is broken" when it means "Docker isn't installed". Build the image first (see [`Container-Developer-Guide.md` §3](../02-building-applications/Container-Developer-Guide.md)) or they will skip.

## 2a. A note on determinism

The suite spawns real processes — control planes, runners, and containers — concurrently. That was a genuine source of intermittent failure until the cause was found: `ControlPlaneTestServer` allocated a port by binding `TcpListener(port 0)`, reading the number, RELEASING it, and handing it to a child that bound it a moment later. Between release and bind, a sibling test class doing the same thing could be handed the same just-freed ephemeral port; the loser's Kestrel failed to bind and its process died with an unhandled exception, surfacing as an unexplained failure in whichever test drew the short straw.

The fix removes the window rather than narrowing it: the CHILD chooses its own port (`--urls http://127.0.0.1:0`) and announces it, and the test reads the address from the "Now listening on" line. The process that picks the port is the one that holds it, so there is no interval to race into.

The child's stdout and stderr are also captured now and quoted in both failure messages. Previously a startup failure produced only "exited early with code -532462766" — which says a .NET process threw, and nothing whatsoever about why.

**Verified by eight consecutive full-suite runs, 800/800**, including one started immediately after a process sweep, which was the condition that used to provoke it most reliably.

One residual flake is on record and not yet explained: on 2026-09-09, in a full-suite run started seconds after the demo processes were killed, `ConcurrentCommandTests.Start_then_stop_for_one_service_are_applied_in_order` timed out at 20 s once; it passed immediately after in isolation (156 ms) and in two consecutive runs of the whole Runner project (2 s each), and the full suite was green on the next run. The shape — one runner process slow to come up while five test assemblies spawn runners, containers and LocalDB in parallel — points at load, not logic. If it recurs, capture the runner's stderr from the failing test before anything else.

A second of the same shape, 2026-09-10: `AgentReconnectTests.An_agent_keeps_receiving_pushes_after_an_outage_longer_than_the_stock_reconnect_window` failed once in a full run (a control plane killed and relaunched on a fixed port, with four other assemblies spawning processes at the same time), passed alone immediately after, and had passed in every earlier full run. Same reading — load, not logic — and the same instruction if it recurs.

Two resource leaks were found while proving it, both of the same shape — cleanup that assumed a killed process is immediately gone:

- **Throwaway LocalDB databases** were dropped only in `DisposeAsync`, never on the start-failure paths. A run where every server failed to start therefore left one database per test; 135 had accumulated.
- **Package blob directories** leaked on roughly a quarter of healthy runs, because `Kill()` only ASKS a process to exit and the OS releases its file handles asynchronously. Disposal now WAITS for the process to be gone before deleting, and the delete retries briefly — the same lesson `AgentHost.StopApplicationAsync` already records one layer up.

## 3. Running the Suite

```bash
dotnet test enList_v3.slnx
```

Or per project, e.g.:

```bash
dotnet test tests/Enlist.ControlPlane.Tests/Enlist.ControlPlane.Tests.csproj
```

No external services need to be started manually — `ControlPlaneTestServer` and `StubAgent` are constructed in-process per test.

## 4. Requirements Traceability Summary

| SRS area | Covered by |
|---|---|
| Agent Registry (§3.1) | `AgentRegistryTests` |
| Placement / Application Policies (§3.2) | `AgentRegistryTests`, `ControlPlaneReconciliationTests` |
| Reconciliation (§3.3) | `ControlPlaneReconciliationTests`, `AgentHostTests`, `AgentCrashRestartTests`, `AgentStartupFailureTests` |
| Status Reporting (§3.4) | `AgentHostTests`, `ControlPlaneReconciliationTests` (heartbeat itself is not currently covered by an automated test — see §5) |
| Package Distribution (§3.5) | `ControlPlanePackageTests`, `PackageRetentionTests`, `PackageCacheTests`, `PackageDistributionTests`, `DeployCliTests` |
| RuntimeFlavor / runner selection (`SAD.md` §9) | `RuntimeFlavorTests`, `RuntimeFlavorRoutingTests` |
| Imperative Control (§3.6) | Not currently covered by an automated test (manually verified via the portal — see §5) |
| Logging (§3.7) | `AgentFileLogSinkTests` |
| Plugin Discovery (§3.8) | `RunnerLifecycleTests` |
| Portal UI (§3.9) | `PolicyConflictDetectorTests`, `TagSelectorMatcherTests` for the portal's resolution logic; rendering and interaction verified manually in a browser (no component-test project) |

## 5. Known Gaps (not currently automated)

- **Heartbeat loop** (`RunHeartbeatLoopAsync`, `AgentHostOptions.HeartbeatInterval`) — verified manually during development (baseline capture, wait, confirm `LastSeenUtc` advances with no other trigger) but has no automated test at this time.
- **Imperative commands** (`AgentCommandRequest` end-to-end delivery) — exercised manually via the portal; no automated integration test exists for the `/api/agents/{name}/commands` → SignalR → runner path.
- **Enlist.Portal components** — `Enlist.Portal.Tests` covers the portal's pure resolution logic; rendering and interaction (dialogs, tables, the wizard) are verified by running the application in a real browser, not by component tests.

- **The protocol-mismatch path through `AgentHost`** — `RunnerProtocol.DescribeMismatch` is unit-tested and the happy path is proven end-to-end, but forcing a real runner to report a WRONG version would mean adding a test-only override to production code. Judged a bad trade; the gap is real and stated here rather than hidden.
- **Named container networks** (`IsolationSpec.Networks`, level 2) — wired through to `docker run --network` and round-trip tested, but no test attaches a real container to a real named network.

These are reasonable candidates for future test-suite investment, roughly in the order listed (the heartbeat is the highest-value gap, since it was the direct cause of a real defect — see [`Runbook.md`](Runbook.md); reconnect behaviour, the other such defect, is now covered by `AgentReconnectTests`).
