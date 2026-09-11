# Runbook / Operations Guide

**Product:** enList v3
**Document status:** Derived from the current implementation and real incidents encountered during development, as of 2026-09-03.

---

## 1. Health Checks

| Check | How |
|---|---|
| Control plane is up | `GET /api/agents` returns `200` (empty array on a fresh database is healthy, not an error). |
| Portal is up | `GET /` returns `200` and renders the Applications page. |
| A specific machine's agent is alive | `GET /api/agents` → find the machine → `lastSeenUtc` within the last ~2–5 minutes (heartbeat interval + staleness threshold). In the portal: the machine's Status chip reads **Online**, not **Offline**. |
| A specific machine's status is *current*, not just *present* | On that machine's detail card in the portal: absence of the amber **Stale** chip. A report can exist and still be stale if it predates the last real change by more than 5 minutes and the heartbeat hasn't yet run again. |
| An application is actually running where expected | Portal → Applications → select it → confirm each expected machine's card shows the application state as `Running` with a non-null `pid`. |

## 2. Standard Operating Procedures

### 2.1 Deploy a new build

See [`Developer-Setup-Guide.md` §6](../01-start-here/Developer-Setup-Guide.md#6-build-and-deploy-a-sample-application) / [`FRS.md` FA-1](../04-requirements/FRS.md#fa-1-deploy-an-application). One `enlist-deploy` invocation per application to upload it; placement is then declared once as a policy rule.

### 2.2 Roll back

Re-run the same `enlist-deploy` command pointed at an older build directory, or `PUT /api/application-policies/{id}` with an older `PackageDigest` directly. This works as far back as the package retention window (default 30 days since it last became unreferenced) — see [`Deployment-IaC.md` §3](Deployment-IaC.md#3-rollback-strategy).

### 2.3 Stop an application everywhere it runs

Portal → Applications → select the row → for each of its assignments, click the row-level Stop action (fans out across every assignment; a tag-selector assignment prompts a confirmation naming every currently-matching machine before applying).

### 2.4 Add a new managed agent

1. Have an Operator mint a join token (`Enlist.ControlPlane.exe create-join-token`; the portal will do this once step 3 of the authentication design lands). Install `enlist-agent` on the new machine pointed at the control plane (see [`Developer-Setup-Guide.md` §5](../01-start-here/Developer-Setup-Guide.md#5-run-an-agent)) and start it **once** with `--join-token <token>`: it enrolls, stores its credential under `--data`, and the option is not needed again — take it out of the service definition. Against a control plane whose authentication is `Off` (loopback only) there is no token to give.
2. It self-registers on its first successful call. Assign tags (portal → Agents → Edit tags, or `PUT /api/agents/{name}/tags`) so it picks up any tag-selector assignments already in place — no redeploy action is needed on the application side.

### 2.5 Decommission an agent

1. Stop and remove the `enlist-agent` Windows Service on the machine itself (outside enList's scope — standard OS-level service removal).
2. In the portal, delete the agent record (Agents → row → Delete icon). This is a registry cleanup, not data destruction, and it revokes the agent's credential: under `Required` the old machine cannot come back as that name without a new join token (under `Off` it reappears on its next contact); rules target tags rather than agent rows, so nothing blocks the delete.
3. Historical status reports and forwarded log lines for that machine name are retained, not deleted, in case the name is reused later.

### 2.6 Investigate "why isn't my change taking effect on agent X"

Decision tree:

1. Is the machine **Online** in the portal? If **Offline**, the agent process itself is down or unreachable — go to §3.1. If Online with a **not receiving** chip beside it, the agent's push connection is down: the change still arrives on its 3-minute re-check, and the chip clears when the connection returns — go to §3.15 if it persists.
2. Is the machine's card showing **Stale**? If yes but Online, the machine is reachable enough to have registered/reported once but its *reconciliation* may not have run since your change — go to §3.2 (the SignalR reconnect issue) if the control plane restarted recently.
3. Confirm the assignment actually changed as intended: `GET /api/application-policies?agentName=<name>` and check `desiredState`/`packageDigest`/`updatedAtUtc` directly against the control plane, bypassing the portal's own caching/polling.
4. If the assignment is correct but the agent's last report predates it: the agent hasn't reconciled yet. Restarting the agent process always forces a fresh, full reconciliation from the current desired state, regardless of any missed push — use this as a reliable (if blunt) fallback while investigating the SignalR path.

## 3. Known Issues and Postmortems

These are real defects found and fixed during this project's development. They are recorded here because each one produces a *specific, recognizable* symptom that is worth being able to name quickly rather than re-diagnosing from scratch.

### 3.1 An agent appears Offline/Stale indefinitely, but its process is genuinely still running

**Symptom:** the portal shows a machine as stale or offline for far longer than the heartbeat interval should ever allow, even though `Get-CimInstance`/Task Manager shows the `enlist-agent` process (and its runner children) are still alive.

**Root cause (fixed):** prior to the heartbeat loop existing, the agent only ever sent a status report **reactively** — on a service/job state change or a reconciliation pass. A perfectly healthy, quiescent application (nothing crashing, no job due, no command received) could go far longer than 5 minutes without producing a new report, which the portal's staleness check would then — incorrectly — read as the agent being gone.

**Fix:** `AgentHost.RunHeartbeatLoopAsync` (default interval 2 minutes) sends the same status snapshot shape on a fixed timer regardless of activity. See [`LLD.md` §9](../03-architecture/LLD.md#9-heartbeat--why-it-exists).

**If this recurs:** confirm `AgentHostOptions.HeartbeatInterval` is actually being scheduled — check the agent's own process is up, and that its `lastSeenUtc` genuinely stops advancing entirely (not just "advances less often than expected"), which would point at a different cause (§3.2 or an actual crash).

### 3.2 A policy change never reaches an agent that appears otherwise healthy

**Symptom:** you update an assignment's desired state; the machine stays Online; but its reported state never changes to match, even after waiting well past any reasonable delay — until the agent process itself is restarted, at which point it immediately picks up the correct state.

**Root cause (fixed):** SignalR group membership (`JoinAgentGroupMethod`) is scoped to a **connection ID**, not an agent name. `HubConnectionBuilder.WithAutomaticReconnect()` re-establishes the underlying connection after a drop (e.g., the control plane restarting) but hands out a **new** connection ID — without re-invoking `JoinMachineGroup` after every reconnect, the agent's connection was silently no longer a member of its own machine's group. Every subsequent `ApplicationPoliciesChanged`/`ExecuteCommand` push landed in a group with nobody listening — `Clients.Group(...).SendAsync(...)` does not error on an empty group, so this failure is completely silent from the server's point of view.

**Fix:** `ControlPlaneAssignmentSource`'s `_hub.Reconnected` handler now re-invokes `JoinAgentGroupMethod` on every reconnect, not only at initial connect. See [`LLD.md` §8](../03-architecture/LLD.md#8-agent-background-loops), [`SAD.md` §5](../03-architecture/SAD.md#5-cross-cutting-design-decisions).

**If this recurs:** the fastest confirmation is to force the exact scenario — restart the control plane, wait for the agent to show as reconnected (`lastSeenUtc` still advancing), then flip one assignment's desired state *without* touching the agent process, and watch whether its next report reflects the change within roughly one push-latency window (a few seconds). If it doesn't, but restarting the agent immediately fixes it, this is the same class of bug.

### 3.3 A stopped application's services still look "Running" and their buttons still look actionable

**Symptom:** you stop an application; its own state chip correctly reads `Stopped`; but the App Services tab still lists every service as `Running` with an enabled-looking Stop button.

**Root cause (fixed):** `StopApplicationAsync` tore down the runner process and updated the *application's own* `state.State` to `Stopped`, but never touched `state.ServiceStates` — the dictionary populated purely by `StateChangedMessage` events while the application was running. With no process left to ever send another such message, the last-known `Running` value for each service stayed in every subsequent status snapshot indefinitely.

**Fix:** `StopApplicationAsync` now also sets every key in `state.ServiceStates` to `Stopped` at the moment it tears the application down. See [`LLD.md` §10](../03-architecture/LLD.md#10-stopping-an-application--service-state-cleanup).

**If this recurs:** check whether the specific stop path that produced the stale state actually went through `AgentHost.StopApplicationAsync` (declarative desired-state stop, or agent shutdown) versus some other code path that might have been added later without the same cleanup step.

### 3.4 An agent reachable only via a tag selector shows the wrong rule/agent counts in one view but not another

**Symptom:** the Applications page shows an application as running on N machines; the Agents page's detail panel for one of those specific machines shows "No assignments for this machine," despite the master list's own **Applications**/**Assignments** columns for that same machine correctly showing non-zero counts.

**Root cause (fixed):** two independent server-side query paths existed for "which rules govern agent X" — the agent-facing `GET /api/agents/{name}/policies` correctly resolved explicit **and** tag-selector matches, but the portal-facing `GET /api/application-policies?machineName=` only ever filtered on `AgentName ==`, silently ignoring tag-selector assignments entirely. A machine reached **exclusively** through a tag selector (no explicit assignment at all) therefore returned zero rows from the portal-facing endpoint while the master list (which resolves selectors client-side, independently, from a full assignment fetch) correctly showed the true count — the two disagreed because they were never using the same logic.

**Fix:** both endpoints now call one shared function, `ResolveEffectivePoliciesForAgentAsync`. See [`LLD.md` §3](../03-architecture/LLD.md#3-tag-selector-matching), [`Database-Design.md`](../03-architecture/Database-Design.md).

**If this recurs:** grep for any new direct `db.ApplicationPolicies.Where(...)` query added outside `ResolveEffectivePoliciesForAgentAsync` — that pattern, on its own, is always the explicit-only bug reappearing.

### 3.5 Portal loads but every static asset is broken (no styling, missing logo, or a raw exception page)

**Symptom:** the portal's HTML loads, but MudBlazor's CSS/JS, the app's own logo image, and/or `blazor.web.js` fail to load — either as empty (0-byte) `200` responses, or as `500`s whose body is a stack trace mentioning `StaticAssetDevelopmentRuntimeHandler` / `FileNotFoundException`.

**Root cause:** launching the compiled DLL directly (`dotnet path/to/Enlist.Portal.dll`) rather than `dotnet run --project ...` resolves the ASP.NET Core **content root** to the current working directory the command happened to be run from, not the project's own folder. `MapStaticAssets()` depends on the content root being correct; when it isn't, hand-added `wwwroot` files silently 200-with-empty-body, and (once content root is fixed by an explicit `--contentRoot` argument) a *separate* dev-mode freshness check (`StaticAssetDevelopmentRuntimeHandler`) then throws for framework/RCL-embedded assets it expects to find physically on disk under that same wrong-relative-to path.

**Fix:** none needed in application code — this is purely a launch-method issue. Always start the portal with `dotnet run --project src/Enlist.Portal/Enlist.Portal.csproj`, executed with the **solution root** as the shell's current directory. See [`Developer-Setup-Guide.md` §4](../01-start-here/Developer-Setup-Guide.md#4-run-the-portal).

### 3.6 "I don't see the agent or control plane process in Task Manager"

**Not a bug** — an operational/observability gotcha worth documenting. Both `Enlist.Agent` and `Enlist.ControlPlane` are typically launched in development as `dotnet.exe <path-to-dll>`, so their **OS process name is literally `dotnet.exe`**, not `enlist-agent.exe`/`Enlist.ControlPlane.exe`. A Task Manager search for "enlist" filters by process/image name and will not match these rows even though they are running — it will, however, match anything published as a genuine self-contained `.exe` (as `Enlist.Portal.exe` and every `enlist-runner`-spawned application process typically are).

**How to actually confirm an agent is alive:** its child runner processes are living proof — per the runner's own pipe-close-equals-shutdown behavior (see [`LLD.md` §7](../03-architecture/LLD.md#7-runner--agent-wire-protocol--messagechanneltouttin)), a runner process cannot outlive its parent agent by more than moments. If you can see `<ApplicationName>.exe` runner processes still running, their parent agent is, definitionally, still alive. To find the agent/control-plane rows themselves, clear any name filter and look through every `dotnet.exe` entry's full command line (Task Manager's Details tab, or `Get-CimInstance Win32_Process | Select ProcessId, CommandLine` in PowerShell).

### 3.7 A `net472` application stays `Failed` and never starts

**Symptom:** an assignment created with a net472 package (flavor auto-detected at upload) reports `state: Failed` in the agent's status report (or is entirely absent from it), and the agent's own log shows either "no runner configured for RuntimeFlavor 'net472'" or a `FileNotFoundException` looking for `enlist-runner.exe`/`enlist-runner.exe.config` under a legacy runner-bin path.

**Root cause, first case:** the agent that reconciled this assignment was started without `--legacy-runner-bin <path>` — `AgentHost` only knows about the runner-bin directories it was explicitly given, one per `RuntimeFlavor` (`net10.0` always maps to `--runner-bin`; `net472` maps to `--legacy-runner-bin` only if that argument was passed). This is deliberate: there is no fallback or guess, so a misconfigured machine fails loudly and immediately rather than silently running the wrong bits.

**Root cause, second case:** `--legacy-runner-bin` points at a directory that doesn't contain a built `src/Enlist.Runner.Legacy` output — either the project was never built (`dotnet build src/Enlist.Runner.Legacy`), or the path points at the wrong configuration/directory.

**Fix:** restart the agent with `--legacy-runner-bin src/Enlist.Runner.Legacy/bin/Debug/net472` (or wherever that project's build output actually lives) after confirming the project builds. See [`Developer-Setup-Guide.md`](../01-start-here/Developer-Setup-Guide.md) for the full agent command line and [`SAD.md` §9](../03-architecture/SAD.md#9-design-vs-implementation) for what the legacy runner does and does not provide (no per-application `AppDomain` isolation between co-hosted net472 applications on the same machine).

## 4. Escalation / Where to Look Next

| Symptom category | Start here |
|---|---|
| Something the portal shows disagrees with something else the portal shows | [`LLD.md` §3](../03-architecture/LLD.md#3-tag-selector-matching) (tag resolution), confirm both call sites still share `ResolveEffectivePoliciesForAgentAsync`. |
| A machine looks wrong/stale/offline when it shouldn't | §3.1, §3.2, §3.6 above. |
| A deploy/redeploy didn't take effect | §2.6 decision tree. |
| The portal itself won't render correctly | §3.5. |
| A test is failing | [`Test-Plan.md`](Test-Plan.md) for what each test asserts and which real component it exercises. |

### 3.8 A `net472` application reports `Running` with no services and no jobs

**Symptom:** the application shows `Running` on the Applications tab, its rule looks correct, and the expanded panel is empty — no services, no jobs, and nothing in the agent log.

**Root cause (fixed, three compounding defects):** discovered by asking a single question of a running system — *what happens if I deploy a net472 application and assign it to a container?*

1. A policy's `RuntimeFlavor` defaulted to `net10.0` **independently of the package it pointed at**. Upload-time detection inspected the actual bytes and got `net472` right — and then never reached the field the agent routes on. Every net472 rule in the demo claimed `net10.0` and was being run by the modern runner.
2. Nothing rejected `net472` + container isolation, though the runner image is Linux .NET 10 and contains no .NET Framework.
3. An assembly that fails to load is **skipped** by discovery — correct, so one bad DLL cannot fail an application, but at the time it was skipped *silently*. So a package whose assemblies could not load produced exactly this symptom: `Running`, empty, no error.

Together they were self-concealing: (1) meant the wrong runner was used, (3) meant the resulting failure was invisible, and (2) meant nothing stopped the combination being created.

**Fix:** a policy now inherits its package's detected flavor (a contradicting claim is rejected); `net472` + container is rejected at create and on any update whose *resulting* combination is invalid; and unloadable assemblies are named in `ReadyMessage.Warnings`, which the agent writes to its log.

**How to recognise it if it recurs:** `Running` with an empty service and job list is almost always a discovery problem, not a supervision one. Check the agent log for `assembly could not be loaded and was skipped`, and compare the rule's `runtimeFlavor` against the package's.

### 3.9 Containers accumulate after an agent is killed

**Symptom:** `docker ps -a` shows stopped `enlist-*` containers that nothing is using, growing by one per application per hard agent restart.

**Root cause (fixed):** a container **outlives the agent that started it**. This is the one place containers are weaker than the process backend, not stronger: a locally-spawned runner is enrolled in a Windows Job Object, so an abnormally-dead agent takes its runners down with it. Nothing equivalent ties a container's lifetime to the agent's. Graceful shutdown removed them; `Stop-Process`, a crash, or Ctrl+C on the wrong window did not.

**Fix:** every container carries an `enlist.agent` ownership label, and on startup — before starting anything, when it demonstrably owns nothing — each agent removes containers still bearing its own label. It logs `Removed N orphaned container(s) left by a previous run of this agent`, which is a signal that the previous run exited uncleanly; the containers are already gone.

**Important:** the reap is scoped by that **label**, never by the `enlist-` name prefix. Several agents commonly share one engine, and a prefix-based sweep by one agent would destroy another agent's *live* containers. Containers created before labelling existed carry no label and will never be reaped — clear those once by hand with `docker rm -f $(docker ps -aq --filter "name=enlist-")`.

### 3.10 Every `net472` application fails with "its runner did not report a protocol version"

**Symptom:** after correcting net472 policies to their true flavor, every such application reports `Failed` with `its runner did not report a protocol version, which means it was built before protocol versioning existed`.

**Root cause (fixed):** `Enlist.Runner.Legacy` keeps its **own duplicated copy** of the protocol types — deliberately, since it shares no assembly with the modern runner. Protocol versioning was added to the modern copy and not to that one, so it reported nothing and the agent correctly refused it.

It had gone unnoticed because defect §3.8(1) meant net472 policies were routed to the *modern* runner, so the legacy runner was never exercised. Fixing one defect is what made the other observable.

**Fix:** both copies carry `RunnerProtocol`, each naming the other, with the rule stated plainly: they change in the same commit.

**The general lesson, which recurs:** enList deliberately duplicates several things — two runner builds sharing no assembly, a portal conflict detector mirroring the control plane's, status DTOs mirrored across an opaque blob. Each duplication is justified. Each is also a place where a fix lands on one side only, and neither the compiler nor the tests will say so. When fixing anything in one of those, check its twin.

### 3.11 Tests fail intermittently with "Enlist.ControlPlane exited early with code -532462766"

**Symptom:** a full-suite run fails somewhere between one and every test in `Enlist.ControlPlane.Tests`, and passes on re-run. Most likely immediately after killing a batch of processes, or on a busy machine. The only diagnosis offered is an exit code.

**Root cause (fixed):** `ControlPlaneTestServer` picked a port by binding `TcpListener(IPAddress.Loopback, 0)`, reading the assigned number, calling `Stop()`, and passing the number to a child process that bound it a moment later. That is a time-of-check/time-of-use race with a real window: the OS will hand the same just-freed ephemeral port to another caller, and several test classes spawn control planes at once. The loser's Kestrel could not bind, the process exited with an unhandled exception — `0xE0434352`, which renders as `-532462766` — and whichever test happened to own it failed for reasons nothing recorded.

**Fix:** the child now chooses its own port (`--urls http://127.0.0.1:0`) and the test reads the address back from Kestrel's `Now listening on:` line. The process that picks the port is the one that holds it, so the window does not exist rather than merely being smaller.

**Also fixed, and worth more than the race itself:** the child's stdout and stderr are captured and quoted in both the early-exit and timeout messages. The old failure text named an exit code and nothing else; any future startup failure now arrives with the server's own explanation attached.

**How to recognise it if something similar recurs:** an exit code of `-532462766` means only "a .NET process threw an unhandled exception". Read the `--- control plane output ---` block in the failure message; if it is missing or empty, the child died before writing anything, which points at process startup rather than at anything the application did.

**Two leaks found while proving the fix**, both from the same wrong assumption — that a killed process is immediately gone. Throwaway databases were dropped only on the success path, so a systematically-failing run left one per test (135 had piled up). And blob directories leaked on healthy runs because `Kill()` only asks: the OS releases handles asynchronously, so deleting immediately races them. Disposal now waits for actual exit, and both failure paths drop the database as well as the directory. Verified by a full run leaving zero of either.

**A trap encountered while fixing this, worth not repeating:** the first attempt captured output with `ReadToEndAsync()`, which returns only at end-of-stream — so for a HEALTHY, still-running server the buffer stayed empty forever and the port line never arrived. Every test then timed out, turning an intermittent flake into a total failure. Reading incrementally with `ReadLineAsync()` in a loop is what makes live output usable.

### 3.12 An application reports `Running` with nothing in it, after pointing a tool at a relative directory

**Symptom:** `enlist-runner --check --app deploy/MyApp` reports zero services and zero jobs for an application that plainly has both, with a warning naming every plugin DLL: `assembly could not be loaded and was skipped — MyApp.dll: ArgumentException — Path "deploy/MyApp\MyApp.dll" is not an absolute path`.

**Root cause (fixed):** `AssemblyLoadContext.LoadFromAssemblyPath` requires a rooted path and throws `ArgumentException` on a relative one. Every plugin assembly was therefore skipped, discovery returned empty, and — on the agent-driven path — the application would report `Running` with nothing in it.

It survived this long because **the agent always passes an absolute path**. Nothing exercised a relative one until `--check` was pointed at a relative directory from a shell, on the first run of the feature.

**Fix:** both runners normalise with `Path.GetFullPath` immediately after the directory-exists check. Pinned by `CheckModeTests`.

**Why this one is worth remembering:** the symptom is indistinguishable from an application whose attributes are wrong — a developer would reasonably spend the next hour re-reading their own `[EnlistService]` declarations. It is also the exact "Running but empty" shape §3.8 describes, reached by a completely different route, which is why `--check` exits `1` when it discovers nothing and prints what to look at.

### 3.13 The portal shows an application as `Failed` that is demonstrably running fine

**Symptom:** an application flickers between `Running` and `Failed` in the portal, or a policy dialog claims no agent has reported it. `report/latest` for that agent alternates between two different snapshots on a poll a couple of seconds apart — one complete, one showing the application `Failed` with a climbing `restartCount` and no services or jobs.

**Root cause:** **two agent processes started with the same `--agent` name.** Nothing rejects this. Both register, both heartbeat, and both POST to `/api/agents/{name}/report`, so "latest" is simply whichever posted most recently. The loser cannot run the applications the winner already owns, so it reports them `Failed` and keeps retrying.

**Fix:** stop the duplicate. There should be exactly one `enlist-agent.exe` per agent name:

```powershell
Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'enlist-agent.exe' } |
  Select-Object ProcessId, CreationDate, @{n='Agent';e={ if ($_.CommandLine -match '--agent\s+(\S+)') { $Matches[1] } }}
```

Two notes on reading that output. `dotnet run` shows as a launcher plus a child, so N logical agents look like 2N rows — match on `enlist-agent.exe` only. And a regex matching the repository path also matches unrelated processes that merely mention it (a shell, an editor, an agent CLI); match on the process **name**, or you will kill something you did not mean to.

**After clearing duplicates the record stays wrong until the survivor's next heartbeat** — `AgentHostOptions.HeartbeatInterval`, 2 minutes by default. A poll immediately afterwards looks like the cleanup failed. Wait one interval before concluding anything.

### 3.14 One service stays `Starting` forever while its siblings run

**Symptom:** after an application starts, one of its services — a different one on each restart — never reaches `Running`. The portal shows it as `Starting` with a green Start button; the other services in the same application are fine. The application's log has a line like:

```
[Error] runner: Unhandled error dispatching StartServiceCommand: System.InvalidOperationException:
Operations that change non-concurrent collections must have exclusive access. A concurrent update
was performed on this collection and corrupted its state.
```

On a `net472` application the runner may instead sit at 100% CPU and answer nothing at all.

**Root cause:** the runner dispatches every command on its own thread, and the agent sends one `StartServiceCommand` per service back-to-back — so several handlers wrote the runner's service registry at the same moment. Runner builds before 2026-09-09 kept that registry in a non-thread-safe dictionary; the losing write threw *before* the service's `[EnlistStart]` was invoked, and nothing retried it. Once corrupted, the same dictionary could also throw during shutdown, so the runner exited with an unhandled exception instead of stopping its services.

**Fix:** fixed in both runners — `RunnerHost` keeps a concurrent registry, serializes Start/Stop per service name, and guards its shutdown path; `ConcurrentCommandTests` pins it. Rebuild both runners and, if any agent runs containerized applications, the `enlist/runner` image too, then restart the affected applications (disable and re-enable the rule, or restart the agent). There is no data to repair.

### 3.15 Every agent stays Online after a control-plane outage, but no policy change reaches any of them

**Symptom:** the control plane was down for longer than about 40 seconds (a migration, a slow deploy, a host reboot). Afterwards every agent still shows Online and its `lastSeenUtc` keeps advancing, but a policy change reaches none of them until each agent process is restarted. On a current build the Agents tab shows **not receiving** beside Online; an older agent shows nothing at all.

**Root cause (fixed):** `WithAutomaticReconnect()` with no arguments retries four times (0, 2, 10, 30 s) and then raises `Closed`, which nothing handled; `WaitForChangeAsync` waited on a signal that only a push or a successful reconnect could release. Heartbeats go over HTTP and recover on their own, so the agent looked healthy.

**Fix:** `ControlPlaneAssignmentSource` reconnects for as long as it takes (ramping to every 30 s), reopens a connection the server closed, re-fetches every 3 minutes regardless, and reports the connection's state with its capabilities (`PushChannel`). `AgentReconnectTests` pins it against a real control-plane restart.

**If this recurs:** the agent log names it — look for `push connection lost` with no later `push connection restored`. Confirm with `GET /api/agents` → `capabilities.pushChannel`: anything but `connected` while `lastSeenUtc` keeps advancing is this condition. The 3-minute re-fetch means a change arrives late, not never; a change that never arrives at all means the agent is on a build older than this fix.

### 3.16 A job runs less often than its cron says

**Symptom:** a job on a short cron fires every second or third tick, or a long batch seems to miss its schedule. The application's log (Applications → the app → Logs) has Warning lines: `job 'X' was due, but run … started Ns ago is still running — this tick is skipped, not queued.`

**Cause (by design):** a job is never run concurrently with itself. When a run takes longer than the interval, the ticks that arrive mid-run are skipped and recorded, not queued. Before this policy the agent started another copy on every tick, without bound.

**Fix:** shorten the work or lengthen the schedule; a Stop on the job cancels the run in flight. If a job fires once and then never again, look for `the due-callback threw` or `the scheduling loop stopped unexpectedly` in the agent log — the loop names its reason now instead of dying silently. A cron that does not parse never gets this far: the API refuses the override, and a declared cron that does not parse is named in the agent log at start.

### 3.17 The control plane is refusing an agent's reports, and nothing said so

**Symptom:** an agent's `lastSeenUtc` stops advancing (or its capabilities chip goes stale) while the agent process is healthy and the control plane answers the portal — it was answering the agent with 500s, or not at all, and the agent's best-effort posture swallowed every attempt.

**What you see now:** the agent log carries `Status report to the control plane failed: …` (and the same for capability reports and forwarded log lines) on the first failure, one line per five minutes while it lasts (`is still failing (N in a row)`), and `succeeded again after N failure(s)` when it recovers. A non-2xx counts as a failure; it used to count as success.

**Fix:** read the detail on the line — connection refused, a 500 with the server's reason, a 413 — and fix the control plane side. The agent needs no restart; it keeps trying on its own cadence.

### 3.18 `docker` stopped answering and the agent froze with it

**Symptom (fixed):** a container agent's reconcile stopped making progress — applications stuck in `Starting`, no new log lines — while the daemon accepted connections and never answered. Every engine call waited on the CLI with no limit.

**Fix:** every `docker` command except `wait` is bounded at 60 s; on expiry the CLI is killed and the start fails with `'docker run' did not finish within 60s — the container engine is not answering`, which goes through the ordinary retry/give-up path. The capability probe reports the same condition as the engine being unavailable. Restart the daemon; the agent picks up on its next retry.

### 3.19 An agent logs `Authentication to the control plane failed`

**Symptom:** the agent's applications keep running, but its `lastSeenUtc` stops advancing, policy changes stop reaching it, and the agent log carries `Authentication to the control plane failed: the control plane rejected this agent's credential (401 on GET /api/agents/X/policies) ...` — once, then at most one line per five minutes while it lasts, and `succeeded again` when it recovers.

**Cause:** the credential was revoked (`revoke-agent`, or `DELETE /api/agents/X`, which revokes as it deregisters), or the agent is talking to a control plane that never issued it (a restored database, a different environment). The `403` variant means the credential file belongs to a different agent name — a data directory copied from another machine.

**Fix:** nothing running is disturbed, so there is no hurry. Have an Operator mint a join token (`create-join-token`), make sure the name is free (`revoke-agent --name X` if it still shows a live credential in the registry), and restart the agent once with `--join-token <token>`: it re-enrolls, replaces `<data>\credential`, and logs `Enrolled as 'X'`. An agent that refuses to start with *rejected this agent's stored credential* is the same condition met at startup, and the same fix. Do not leave `--join-token` in the service definition afterwards.
