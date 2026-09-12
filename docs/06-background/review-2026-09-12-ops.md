# Regression review 2026-09-12 — operations area (scripts, ops docs, developer guides, config)

Reviewed against the 3.0.0 truths: Agent / Application Policy / Tag terminology, SQL Server + EF Core 10
with the single `20260911192322_InitialCreate` migration, authentication fully implemented on 2026-09-11
(C2 closed), 232 tests (15 skipped here), and the demo topology in `demo/start-demo.ps1`.

Findings are one line each: `severity | file:line | quoted text | fix`.

## Group 1 - the three scripts

script-bug | demo/start-demo.ps1:102 | `& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $demoDb up | Out-Null` | `Out-Null` swallows the first-run image pull, which demo-db.ps1:297-300 deliberately streams ("Pulling $image (about 1.5 GB, once)") precisely so it does not "look hung for the minutes a 1.5 GB image takes". On a clean machine start-demo.ps1 sits silently at "Demo SQL Server..." for those minutes. Let the child's output through (or tee it), keeping only the connstring call quiet.

script-bug | demo/start-demo.ps1:123-126 | `throw "The control plane did not answer on $controlPlaneUrl within 60s."` | The control plane child (and its hidden `cmd.exe`) was already started at :117 and is left running when this throws — the next run's Stop-DemoProcesses is the only thing that ever cleans it up, and a developer who fixes the migration and re-runs gets the stale process's log tail. Wrap the bring-up in try/finally (or call Stop-DemoProcesses before throwing).

script-bug | demo/start-demo.ps1:106,111,115,153,157 | `$env:Authentication__Mode = 'Off'` / `$env:ASPNETCORE_ENVIRONMENT = 'Development'` | All five environment variables are set in the CALLER's shell and never restored, so after the script returns that shell holds `Authentication__Mode=Off`, `ASPNETCORE_ENVIRONMENT=Development` and a `ConnectionStrings__ControlPlane` containing the SA password. A control plane started by hand from the same shell afterwards silently inherits auth-off — the exact accident the comment at :112-114 says cannot happen. The comment at :108-110 already shows the leak is known to bite in the other direction. Save and restore in a `finally`.

security | demo/sql-server/demo-db.ps1:333 | ``Write-Host "  `$env:ConnectionStrings__ControlPlane = '$(Get-ConnectionString -Values $values)'"`` | The `up` action prints the whole connection string — `Password=<SA password>` included — to the console, so it lands in any transcript, scrollback or CI log. `connstring` exists for when the caller actually wants it; `up` should print `./demo-db.ps1 connstring` instead.

script-bug | demo/demo-status.ps1:78,91 | `$inspect = & $wslc inspect enlist-demo-sql --format json 2>$null` and `(& $wslc list 2>$null)` | Redirecting a native command's stderr while `$ErrorActionPreference = 'Stop'` (:13) is exactly the Windows PowerShell 5.1 NativeCommandError trap that demo-db.ps1:85-92 documents at length and routes every single wslc call through `Invoke-Wslc` to avoid — and it notes `wslc inspect` on a container that does not exist "writes to stderr routinely", which is the normal case for a status check before the first `up`. demo-status.ps1 should use the same drop-to-`Continue` wrapper rather than a bare `2>$null`.

script-bug | demo/start-demo.ps1:61-62 | `foreach ($p in (Get-CimInstance Win32_Process | ...)) { Stop-Process -Id $p.ProcessId -Force }` | A process that exits between the CIM query and the `Stop-Process` makes `Stop-Process` throw, and with `$ErrorActionPreference = 'Stop'` (:43) that aborts the whole script at its very first step — including the `-Stop` path. Add `-ErrorAction SilentlyContinue`.

contradiction | demo/sql-server/demo-db.ps1:334-336 | `dotnet run --project src/Enlist.ControlPlane` … "The database and schema are created on that first run (Development auto-migrates)." | start-demo.ps1:11-14 states the opposite workflow for the same database — the demo control plane runs in Production and "refuses to start on a pending migration rather than applying it", so migrations must be applied with `dotnet ef database update --project src/Enlist.ControlPlane`. Two scripts in the same folder hand the operator different bring-up instructions; demo-db.ps1 should name the `dotnet ef database update` route (or say explicitly that only a Development `dotnet run` migrates).

style | demo/demo-status.ps1:72-98 | `Write-Host "Database (wslc)"` / `Write-Host "Application containers (wslc)"` | Hard-wired to wslc, but start-demo.ps1:35-36 offers `-ContainerEngine docker`; a demo brought up with `-ContainerEngine docker` reports no application containers at all, and when `wslc` is absent (:74-76) the "Application containers" heading is never printed, so the section silently disappears rather than saying why. Take the engine as a parameter, or at least print the heading with "(engine not available)".

style | demo/start-demo.ps1:91 | `[regex]::Matches($json, '"state":"([A-Za-z]+)"')` | Counts the application-level `state` and every per-service `state` in the same tally (AgentStatusSnapshot.cs has `State` on both `ApplicationStatusEntry` and `ServiceStatusEntry`), so the "2 Running, 1 Stopped" printed at :168-169 mixes applications with services. It also silently depends on the control plane storing the agent's raw camelCase body (Program.cs:285-286) — `[regex]::Matches` is case-sensitive, unlike PowerShell's `-match`. Parse the JSON and count applications only.

style | demo/start-demo.ps1:134-138 | `$common += @('--join-token', $JoinToken)` | The join token goes onto both agents' command lines, where any local user can read it out of `Win32_Process` for the life of the agent (demo-status.ps1 reads exactly that table). Passing it through an environment variable or a file would keep a single-use enrolment secret off the process list.

Everything else in the three scripts checks out: no `&&`, no ternary, no `??`, no here-strings, every path
interpolated into the `cmd /c` line is quoted (start-demo.ps1:78,117,133,139-140,158), the process
matching really does require DLL *and* demo argument, and the `-Stop`/`up`/`down`/`reset`/`status`/
`connstring` surfaces match the documented behaviour. The agent flags used at :133,139-140
(`--control-plane`, `--agent`, `--join-token`, `--runner-bin`, `--legacy-runner-bin`, `--data`,
`--container-image`, `--container-engine`) all exist in src/Enlist.Agent/Program.cs:97, and
`ControlPlane__BaseUrl` at :153 matches src/Enlist.Portal/Program.cs:59.

## Group 2 - Runbook.md and Test-Plan.md

### Runbook.md

wrong | Runbook.md:212-216 | `Get-CimInstance Win32_Process \| Where-Object { $_.Name -eq 'enlist-agent.exe' }` … "match on `enlist-agent.exe` only" | §3.6:113 says the exact opposite about the same processes — "their **OS process name is literally `dotnet.exe`**, not `enlist-agent.exe`" — so §3.13's duplicate-agent recipe returns nothing in the development setup §3.6 describes, which is the setup where duplicates actually happen (demo/start-demo.ps1:61 and demo/demo-status.ps1:43 both match `dotnet.exe` plus a command-line fragment for this reason). Match `dotnet.exe` with `--agent` on the command line, and mention `enlist-agent.exe` only for a service install.

structure | Runbook.md:127-135 | `## 4. Escalation / Where to Look Next` | Section 4 sits in the MIDDLE of section 3 — §3.8 through §3.20 run from :137 to :283, after it — so the escalation table is stranded mid-chapter and its "start here" column names only §3.1, §3.2, §3.5, §3.6, ignoring the thirteen entries printed below it (including §3.19/§3.20, the two an operator will hit first on a `Required` control plane). Move §4 to the end of the file and add rows for authentication symptoms.

wrong | Runbook.md:16 | "confirm each expected machine's card shows the application state as `Running` with a non-null `pid`" | `Pid` is 0 for a containerised application by design — AgentStatusSnapshot.cs, ApplicationStatusEntry.RuntimeId: "note Pid above is 0 for a container" — and Enlist.Agent.Tests' `ContainerRunnerBackendTests` pins `Pid = 0`. This health check therefore reads as a failure for every container-isolation application. Check `runtimeId` (or the portal's container chip) instead, and say `pid` applies to process isolation only.

missing | Runbook.md:10-16 | "Control plane is up \| `GET /api/agents` returns `200`" | `/health` (src/Enlist.ControlPlane/Program.cs:147-204) is the endpoint built for precisely this — it reports the version, the newest applied migration, whether the database answers (200/503) and the authentication mode, and is reachable anonymously — while `GET /api/agents` is Viewer-policed (Authentication/EndpointPolicies.cs) and answers **401** under `Authentication:Mode=Required`, so the table's first row now fails on any real deployment. Lead with `GET /health`, and note the key `GET /api/agents` needs.

wrong | Runbook.md:77 | "without re-invoking `JoinMachineGroup` after every reconnect" | No such method exists: it is `JoinAgentGroup` on the hub (src/Enlist.ControlPlane/Hubs/ApplicationPolicyHub.cs:26), invoked as `ApplicationPolicyHubContract.JoinAgentGroupMethod` (src/Enlist.Agent/Configuration/ControlPlaneAssignmentSource.cs:153,228) — the same paragraph spells it correctly one sentence earlier. Pre-rename leftover.

wrong | Runbook.md:30 | "for each of its assignments, click the row-level Stop action (fans out across every assignment; …)" | The portal has no Stop action on a policy-rule row: it is an Enabled/Disabled **switch** per rule (src/Enlist.Portal/Components/Pages/ApplicationPolicyScreen.razor:69-70, labelled Enabled/Disabled on purpose — see its comment at :122), applied one rule at a time, and nothing fans out across rules. The confirmation naming every currently-matching agent is real but belongs to that switch on a tag-selector rule (src/Enlist.Portal/Services/ApplicationPolicyStateToggler.cs:43-55). Reword as "set each of the application's policy rules to Disabled".

contradiction | Runbook.md:109 | "**Fix:** none needed in application code … Always start the portal with `dotnet run --project src/Enlist.Portal/Enlist.Portal.csproj`" | demo/start-demo.ps1:158 starts the portal exactly the way §3.5 forbids — `dotnet …\Enlist.Portal.dll --urls http://localhost:5231` — and it works, because the script sets `ASPNETCORE_ENVIRONMENT=Development` and a correct working directory (its comment at :154-156 gives the environment, not the content root, as the deciding factor). One of the two accounts of the root cause is incomplete; record the working DLL recipe here so the demo and the runbook agree.

stale | Runbook.md:4 | "Derived from the current implementation and real incidents encountered during development, as of **2026-09-03**." | The file now documents the whole authentication surface that landed 2026-09-11 (§2.7 keys and roles, §3.19 agent credentials, §3.20 the portal's own key). Restate the date.

stale | Runbook.md:14,15,16,30,35,40,41,47,48,65,93,95,97,132 | "machine", "assignment(s)", "`GET /api/application-policies?machineName=`" | v3 vocabulary is Agent / Application Policy (policy rule) / Tag, with Assignment reserved for src/Enlist.Agent's own descriptor. §3.4:97 even quotes a query parameter that is and was `agentName` (src/Enlist.ControlPlane/Program.cs:485), and §2.6:49 uses the right one — the two paragraphs disagree with each other. Sweep the file; the §3.x postmortem narratives can keep the old words only where they are quoting a historical symptom, and should say so.

style | Runbook.md:153-161,262-266 | "§3.9 Containers accumulate…" / `docker ps -a` / `docker rm -f $(docker ps -aq --filter "name=enlist-")` / "§3.18 `docker` stopped answering" / "every `docker` command except `wait` is bounded at 60 s" | Both behaviours live in the engine-agnostic `CliContainerEngineBase` (src/Enlist.Agent/Supervision/CliContainerEngineBase.cs:21, `CommandTimeout` default 60 s) and so apply identically to `--container-engine wslc`, which is now the demo default (demo/start-demo.ps1:36) and has its own test class. Give the `wslc` equivalents, or write "your engine's CLI".

Verified correct and left alone: the 30-day package retention window at :26 (PackageRetentionOptions.RetentionPeriod = TimeSpan.FromDays(30)); the ~2-5 minute liveness window at :14 (HeartbeatInterval 2 min, portal `IsOnline` < 5 min in Agents.razor:260); the 2-minute heartbeat at :218; the 3-minute re-fetch at :47 and :244 and the "ramping to every 30 s" reconnect at :242 (ControlPlaneAssignmentSource.cs:53,322-326); §2.4's join-token flow including the portal's *Enroll agent* dialog (EnrollAgentDialog.razor:40,64); §2.7's CLI spellings (`create-api-key --name portal --role Operator --expires never`, ManagementCli.cs:233-238); and — checked carefully because the doc asserts it twice — §2.5:40 and §3.19:272 are RIGHT that deleting an agent revokes its credential, via the cascade FK `FK_AgentCredentials_Agents_AgentName` (ControlPlaneDbContext.cs:35, Migrations/20260911192322_InitialCreate.cs:150-155), even though the endpoint handler itself never mentions credentials.

### Test-Plan.md

wrong | Test-Plan.md:131 | "**Verified by eight consecutive full-suite runs, 800/800**" | 800 is eight runs of a 100-test suite; the suite is 232 tests (:5), so eight full runs are 1,856. Left over from an earlier era. Restate as the number of runs, or re-derive the total.

contradiction | Test-Plan.md:154 | "No external services need to be started manually — `ControlPlaneTestServer` and `StubAgent` are constructed **in-process** per test." | :21 says the opposite — "Spawns the **real** `Enlist.ControlPlane.exe` against a throwaway LocalDB database per test class" — as do :11, :71 ("a real `enlist-runner` process"), and the whole of §2a, whose subject is a child process choosing its own port. It also glosses over LocalDB, which genuinely must exist on the machine. Say "started for you, as real child processes; you need only LocalDB (and a container engine for §2.6)".

contradiction | Test-Plan.md:119,121 vs :5 | "The **16** container tests call `Skip.IfNot(...)`" + "**Three** `PortalAuthenticationTests` … skip themselves" vs ":5 15 skip themselves" | 16 + 3 = 19, not 15. Both numbers are individually right — 12 Docker skips across the four Docker classes (ContainerRunnerBackendTests 4, ContainerPortPublishingTests 5, ContainerOrphanReapTests 2, ContainerCrashRestartTests 1) plus 4 in `WslContainerEngineTests`, which do NOT skip on a machine that has `wslc` and `enlist/runner:wslc`. Say so: "up to 19 can skip; 15 do on a machine with wslc and its image but no Docker".

missing | Test-Plan.md:158-169 | §4 Requirements Traceability table | No row for authentication or access control, though six classes cover it — `AuthenticationTests`, `AccessEndpointsTests` (§2.1), `AgentCredentialTests` (§2.2), `DeployCliTests`' key cases (§2.4), `PortalAuthenticationTests`, `PortalRolesTests`, `ControlPlaneApiClientTests`, `ProtectedSettingsTests` (§2.5). Add the row (SRS has no authentication section to point at either, which is the deeper gap).

stale | Test-Plan.md:5 | "rewritten **2026-09-08** at class level after the per-test list drifted" | The body is newer than its own status line: :103 describes "C2 step 3" (2026-09-11), :135 records a flake from 2026-09-10, and §2.1/§2.2/§2.5 carry the authentication classes added on 2026-09-11. Restate the date.

style | Test-Plan.md:160-161 | "Agent Registry (§3.1)", "Placement / Application Policies (§3.2)" | The SRS sections these cite are still titled "Machine Registry" and "Placement (Assignments)" (docs/04-requirements/SRS.md:100,108), so the traceability table silently renames its own source. Out of this group's scope, but the two files should be fixed together.

style | Test-Plan.md:119 | "the four Docker classes" | `DockerContainerEngineTests` is a fifth Docker-named class with a `Skip.IfNot` (tests/Enlist.Agent.Tests/DockerContainerEngineTests.cs:33) — it skips on non-Windows only, never for a missing engine, which is worth the half-sentence since :51 already advertises it as the "no real engine needed" one.

Verified correct: the per-project totals at :19,:42,:69,:93,:99 (72 + 72 + 50 + 5 + 33 = 232, matching :5); the §5 gap list — no test anywhere mentions `Heartbeat` or `AgentCommandRequest`, so both gaps are still real; and §2a's account of the port race, which matches the `--urls http://127.0.0.1:0` fix in tests/Enlist.TestSupport/ControlPlaneTestServer.cs.

## Group 3 - Deployment-IaC.md, Installer-UI-Design.md, Demo-Install-Design.md

(Excluded as already known: Deployment-IaC.md:73 quoting the deleted migration name, Deployment-IaC.md:289 "there is no auth layer at all", and Demo-Install-Design.md:31,143,198 arguing from "enList has no authentication".)

### Deployment-IaC.md

wrong | Deployment-IaC.md:232-243 and :249-275 | §4.1 `EXPOSE 8080` + `ENTRYPOINT ["dotnet", "Enlist.ControlPlane.dll"]`, §4.2 `ports: ["5293:8080"]` / `ControlPlane__BaseUrl: "http://controlplane:8080"` | Neither example can start today. The aspnet:10.0 image defaults `ASPNETCORE_URLS` to `http://+:8080` — not loopback and not HTTPS — and neither example sets `Authentication:Mode`, which means `Required`: §1.8:200 says plain `http://` off loopback refuses to start, and `Off` would refuse too because `+` is not loopback (:199). The portal service in §4.2 hits the identical pair of rules and additionally has no `ControlPlane:ApiKey`, so it could only talk to a control plane running `Off`. Add TLS (or an explicit loopback-plus-reverse-proxy note) and a key to both examples, or mark §4 as predating §1.8.

wrong | Deployment-IaC.md:281 | "gate on all **121 tests** passing (see [`Test-Plan.md`](Test-Plan.md))" | The suite is 232 (Test-Plan.md:5). 121 is two rewrites old.

stale | Deployment-IaC.md:176 | "A working container engine \| **Docker today.** The agent probes it (`docker version` against the **server**) on every heartbeat" | wslc is a shipped second engine, not a future one: `--container-engine docker|wslc` (src/Enlist.Agent/Program.cs:97,114-118), its own `WslContainerEngine` and `WslContainerEngineTests`, and it is what the demo defaults to (demo/start-demo.ps1:36). §1.3:47 in this very file already lists both. Say "Docker or wslc", and give the `wslc build` line beside :181's `docker build`.

contradiction | Deployment-IaC.md:20,23 | "Runs as a Windows Service, under IIS, or **in a container** — see §1.6" | §1.6 is titled "Hosting Modes (Windows, **outside containers**)" and says nothing about containers; the header note at :8 says only the runner has a Dockerfile; §4.1 offers a control-plane Dockerfile as an explicit not-implemented proposal; and Installer-UI-Design.md:212-214 states flatly "none of it exists yet". §1's job is "the actual current state" (:10), so drop the container option from these two rows or point them at §4.

stale | Deployment-IaC.md:4 | "Originally 2026-09-03; revised **2026-09-08** — §1.4 …, §1.6 … and §1.7 … were added or rewritten" | §1.8 (the whole authentication section, dated 2026-09-11 in its own first line) is newer than the status line that lists what changed. Add the 2026-09-11 revision.

stale | Deployment-IaC.md:90 | "the **three** that had accumulated during development were collapsed into one once it was established that no existing database needed preserving" | This narrates the 2026-09-08 collapse; the migrations have since been collapsed twice more, most recently on 2026-09-11 (`20260911192322_InitialCreate`, the eight-table schema with AgentCredentials/JoinTokens/ApiKeys). Fix alongside the known :73 error, and name the current migration id so the pair cannot drift again.

missing | Deployment-IaC.md:47 | "`--control-plane`, `--agent`, `--runner-bin`, `--data`, `--legacy-runner-bin`, `--container-image`, `--container-engine`" | `--join-token` is missing from the one table that claims to be the agent's whole configuration surface, even though §1.8:202 devotes a paragraph to it. `--assignments` (the control-plane-less mode) is missing too. Both are in the usage line at src/Enlist.Agent/Program.cs:97.

style | Deployment-IaC.md:252 | `image: mcr.microsoft.com/mssql/server:2022-latest` | The only SQL Server image this repository actually runs is `mcr.microsoft.com/mssql/server:2025-CU3-ubuntu-24.04` (demo/sql-server/demo-db.ps1:69). Matching them costs nothing and keeps one supported version in play.

Verified correct and left alone: every default in §1.3 — `PackageStorage:MaxUploadBytes` 512 MB against the portal picker's 500 MB (Program.cs:63, UploadPackageDialog.razor:97), PackageRetention 30 days / 6 hours, LogRetention 3 days / 1 hour, ReportRetention 1 day / 1 hour, and `docker` as the agent's default engine (Program.cs:114); §2:210's claim that only `appsettings.Development.json` exists beyond the base files (confirmed — four appsettings files in total under src/); §1.6's content-root account, which matches Program.cs:38-42; and the whole of §1.8 against the implementation, including the `DELETE /api/agents/X` revocation claim at :202 (cascade FK).

### Installer-UI-Design.md

stale | Installer-UI-Design.md:234 | "An *initial administrator* page is reserved in the flow … and appears **only once C2 is implemented**; until then the Finish page carries the **trusted-network warning** verbatim from Deployment-IaC. The installer should be one of the reasons to resolve C2" | C2 closed on 2026-09-11 — which the very next paragraph (:236) says. Leaving the superseded paragraph in place above its own correction means §8's table row and its prose disagree, and a reader skimming for "what does the Finish page say" gets the wrong answer. Rewrite the paragraph rather than appending to it.

wrong | Installer-UI-Design.md:119,131,160,264,266 | `Listen on [http://+:5293]`, `CP_URLS = http://+:5293`, portal "listen `http://+:5231`", and the silent example's `CP_URLS=http://+:5293 … PORTAL_URLS=http://+:5231` | These defaults produce services that refuse to start: `+` is not loopback, so `Off` is rejected, and under `Required` (the default) a non-loopback plain-HTTP listener is rejected too — the rules this document itself cites at §12.6 and §8's update. The page needs an `https://` default and the certificate field §12.6 promises; the silent example needs the same.

wrong | Installer-UI-Design.md:192 | "A second call, `GET {url}/api/agents/{name}`, warns if the agent name is already registered" | That endpoint is Viewer-policed (src/Enlist.ControlPlane/Authentication/EndpointPolicies.cs; Program.cs:385), and the installer has no credential at that moment — it holds a join token at best, which only `POST /api/agents/enroll` accepts. The call returns 401 under `Required`, so the "already registered" warning silently never fires on exactly the deployments that need it. Either drop the check, or get the duplicate-name answer from the enrollment call itself, which already answers 409 for a name holding a live credential (Program.cs:233-240).

style | Installer-UI-Design.md:181,273 | `Image [enlist/runner:3.0.0]`, `AGENT_IMAGE=enlist/runner:3.0.0` | No such tag is produced anywhere: the repository builds `enlist/runner:dev` (Deployment-IaC.md:181) and `enlist/runner:wslc` (demo/start-demo.ps1:129). Fine as a proposal for a release tag, but §4's "where it describes current product behaviour it is accurate to the code" invites the reader to trust it — say which it is.

### Demo-Install-Design.md

wrong | Demo-Install-Design.md:30 | "The control plane's built-in default connection string is SQL Server Express **LocalDB** ([`Program.cs:40`](../../src/Enlist.ControlPlane/Program.cs))" | Program.cs contains no connection string at all — line 40 is `ContentRootPath` in the `WebApplicationOptions`. The LocalDB default lives in src/Enlist.ControlPlane/appsettings.json:3. Cite that file (and prefer a symbol to a line number, which rots).

contradiction | Demo-Install-Design.md:108 vs demo/start-demo.ps1:121 | "Wait for `GET /health` → **200** — the endpoint added on 2026-09-11 for exactly this kind of caller" | The script the document calls its own specification (:188 "when the launcher and a script disagree, one of them has a bug") polls `GET /api/agents` instead, which is Viewer-policed and would answer 401 under `Required`. `/health` is the right probe; fix the script, and the two stop disagreeing.

contradiction | Demo-Install-Design.md:172 vs Test-Plan.md:166,174 | "Ten minutes, and **every claim in it is backed by a test** in [`Test-Plan.md`](Test-Plan.md)" | Step 4 of the walkthrough (:165) is imperative Start/Stop of a service and a job, which Test-Plan lists twice as NOT automated ("Imperative Control (§3.6) \| Not currently covered by an automated test", and §5's second gap). Either soften the sentence or close the gap.

contradiction | Demo-Install-Design.md:44 | "at **1.8 GB** it would triple the installer" | demo/sql-server/demo-db.ps1:299 tells the operator the same image is "about 1.5 GB". One of the two numbers is wrong; they are describing the same pull.

style | Demo-Install-Design.md:51 | "`enlist/runner:<version>`, bundled as a tarball" | Same tag question as Installer-UI-Design.md:181 — the real tags are `:dev` and `:wslc`. Decide the release tag once and use it in both documents.

Verified correct: the demo topology at :47-50 (tags `env=demo`/`dev=test9`/`dev=test10`, container on DEV-AGENT-02, `enlist/runner` on wslc) against demo/start-demo.ps1:129,139-140; every seed call at :111 (`PUT /api/agents/{name}/tags`, `POST /api/packages?application=`, `POST /api/application-policies`) against the endpoint list in Program.cs; `wslc kill` at :167, which is exactly what tests/Enlist.Agent.Tests/WslContainerEngineTests.cs:139 does; and §11's table, which matches what the three scripts actually do.

## Group 4 - Application-Developer-Guide.md and Container-Developer-Guide.md

### Application-Developer-Guide.md

wrong | Application-Developer-Guide.md:420 | "Inside `--dev`: `list`, `start <svc>`, `stop <svc>`, `run <job> [k=v]`, **`pause <job>`, `resume <job>`**, `quit`, `help`." | The dev host has no `pause` or `resume` verb — its command switch (src/Enlist.Runner/Dev/DevHost.cs:451-485) handles `help`/`?`, `list`/`services`/`jobs`, `start`, `stop`, `run`, `cancel`, `quit`/`exit` and nothing else, so both print "unknown command". The quick reference also drops `cancel`, which does exist and which §4:133 and §4:145-155 spend a page on. Replace `pause`/`resume` with `cancel`.

The rest of this guide is clean. Checked line by line against the code: the `--check` exit-code contract and its "Nothing was discovered" text (§3), the whole `--dev` command table at :128-134, the mode-exclusivity message at :400 (src/Enlist.Runner/Program.cs:100, verbatim), the tick-skip log line at :380 (AgentHost.cs:1002), the cron dialect rules at :378, the `Ledger Rebuild` / `Legacy Batch` / `Tax Rate Refresh` / `Nightly Reconciliation` job names (samples/Enlist.Sample.OrderProcessor/Jobs.cs:5,27,59 and samples/Enlist.Sample.Legacy/LegacySample.cs:63), and — checked because it is quoted as a file — the launchSettings JSON at :285-307, which is byte-for-byte samples/Enlist.Sample.OrderProcessor/Properties/launchSettings.json, with all four samples carrying the same three profiles in the same order. The net472 `--log-window` advice at §7 is right too: src/Enlist.Runner.Legacy/Program.cs:59-72 supports it.

### Container-Developer-Guide.md

wrong | Container-Developer-Guide.md:619 | "`AgentApplicationStatusDto` still carries only `Pid`, so the portal shows nothing useful for a containerized app **until C3 surfaces `RuntimeId`**" | Contradicted twice in this same file — :62 ("The agent status report also carries `IsolationMode` … and `RuntimeId`") and :617 ("Both tooltip `docker logs <short id>` from `RuntimeId`") — and by the code: ApplicationStatusEntry carries `IsolationMode`, `RuntimeId` and `Endpoints` (src/Enlist.Agent/Status/AgentStatusSnapshot.cs), and the portal renders all three. Only the first sentence of the bullet ("`Pid` is `0` for containers", deliberate) still holds; delete the rest.

wrong | Container-Developer-Guide.md:152 | "Start the control plane on `--urls http://0.0.0.0:5293` so it is reachable from outside loopback." | That control plane cannot start today: `0.0.0.0` is not loopback, so `Authentication:Mode=Off` is refused, and the default `Required` refuses plain `http://` off loopback (Deployment-IaC.md §1.8). The Traefik walkthrough this line supports therefore cannot be reproduced as written. Use `https://` with a certificate and a Viewer key on the Traefik provider, or run the whole recipe on loopback with the engine reaching it another way.

wrong | Container-Developer-Guide.md:574 | "Or the whole suite (**121 tests**)" | 232 (Test-Plan.md:5). Same stale figure as Deployment-IaC.md:281 — they were presumably written together.

contradiction | Container-Developer-Guide.md:210 vs :227-243 | "**Prerequisites** 1. **Docker Desktop**, running, in **Linux container** mode." | §2a offers wslc as a full alternative ("same seam, same image, same lifecycle", pinned by `WslContainerEngineTests`), and wslc is what the demo actually uses (demo/start-demo.ps1:36,129). §2 should read "Docker Desktop **or** WSL 2.9.11+ with `wslc` (§2a)", or a reader following the guide top-down installs Docker Desktop unnecessarily.

contradiction | Container-Developer-Guide.md:275-289 | "**The exact command the agent issues**, written out: `docker run -d -p 127.0.0.1:0:5000 -v … enlist/runner:dev --listen 5000 --app /app`" | Not exact: the agent also passes `--name enlist-<app>-<8 hex>` and the two labels `enlist.agent` / `enlist.application` (src/Enlist.Agent/Supervision/ContainerRunnerBackend.cs:72), which §8.1:413-417 calls "the ownership record" and §8.2:515 calls load-bearing for the reap. Someone copying this command by hand creates a container no agent will ever reap — the exact trap §8.2:517 warns about. Add the name and labels, or drop "exact".

style | Container-Developer-Guide.md:383 | "Every engine command except `wait` is bounded at 60 s (`DockerContainerEngine`'s `commandTimeout`)" | The bound lives in the shared `CliContainerEngineBase` (:21, `CommandTimeout`), which :607 in this same file correctly credits as "what both engines share" — so this applies to `wslc` too, and `wslc` has no `wait` verb at all (:240). Attribute it to the base class.

style | Container-Developer-Guide.md:46-51,73-79,126,139 | The `curl -X POST http://localhost:5293/api/application-policies …` examples | None carry an `Authorization` header, so each one is a 401 against any control plane not running `Authentication:Mode=Off` on loopback. One sentence at the top of §1a saying "these assume the loopback developer setup; add `-H \"Authorization: Bearer <key>\"` otherwise" would cover every example in the file.

style | Container-Developer-Guide.md:93 | `"image": "enlist/runner:3.2.0",       // pin THIS application to one runner release` | An illustrative tag above the product's own version (3.0.0, Directory.Build.props) and unrelated to the two tags that exist (`:dev`, `:wslc`). Pick a plausible one so the example does not read as a real release.

style | Container-Developer-Guide.md:4 | "Every command here was run on Windows 11 + Docker Desktop 29.7.2 (WSL2, Linux containers) on **2026-09-07**" | §2a's wslc material is newer than that and was verified on a different engine ("Verified against wslc 2.9.11.0"). Note the second verification date.

Deliberately NOT flagged: the "the control channel is unauthenticated" statements at :359-361 and :620 are still TRUE — C2 authenticated the control plane, agents, portal and tools, not the runner's own control channel, which is confined by loopback-only publishing. Also verified correct: the Dockerfile base image at :621 (`mcr.microsoft.com/dotnet/runtime:10.0`), the 5-minute endpoint staleness at :132 (Program.cs:1277), the container naming scheme at :409, the four Docker test classes at :554-561, and every wslc difference listed at :237-241.

## Group 5 - Aware-Architecture-Notes.md and the four READMEs

### demo/sql-server/README.md

stale | demo/sql-server/README.md:162 | "`MigrateAsync` created `EnlistControlPlane`, **all five tables** and `__EFMigrationsHistory` with **the three migrations**" | The schema is eight tables (Agents, AgentCredentials, AgentLogs, AgentReports, ApiKeys, ApplicationPolicies, JoinTokens, Packages) under a SINGLE migration, `20260911192322_InitialCreate`. The verification run is dated 2026-09-10, before the authentication tables and the final collapse, so the numbers were true then and read as current now. Either re-run the check or date-stamp the row.

stale | demo/sql-server/README.md:164 | "the schema and **all three migrations** survived the cycle" | Same: one migration today. Same fix.

wrong | demo/sql-server/README.md:67 | "creates the `EnlistControlPlane` database and its schema on first connect ([`Program.cs:69`](../../src/Enlist.ControlPlane/Program.cs))" | Line 69 is `AddHostedService<PackageRetentionSweepService>()`. The Development auto-migrate branch is src/Enlist.ControlPlane/Program.cs:96-99 (`if (app.Environment.IsDevelopment()) … Database.MigrateAsync()`). Cite the branch, not a line number.

contradiction | demo/sql-server/README.md:27 vs demo/sql-server/demo-db.ps1:299 | "~**1.8 GB** of disk for the image" vs the script's "Pulling $image (about **1.5 GB**, once)" | Two documents agree on 1.8 GB (this one and Demo-Install-Design.md:44), so the script's message is the outlier a user actually sees. Fix the script.

Otherwise sound: the LocalDB/container split and its enforcement claim (ControlPlaneTestServer really does hardcode `(localdb)\mssqllocaldb`), the `down`/exit-code account, the no-restart-policy note that Installer-UI-Design.md:216 leans on, and the Git Bash warning at :148-151, which matches how `wslc exec` mangles `/opt/...` paths under MSYS.

### demo/README.md

contradiction | demo/README.md:98 | "The demo's shape is two tagged agents and **one policy rule per application**, targeted by tag" | The table immediately below has five rules for four applications — OrderProcessor appears twice, once per agent, and that second container rule is the whole point of having two agents (:111). Demo-Install-Design.md:16 says "five policy rules". Say "five rules across four applications".

Everything else here matches the scripts and the code: the fleet table, the `-Stop` / `-ContainerEngine` / `-JoinToken` flags, the enrollment walkthrough (the log lines it quotes are real — `Enrolled as '{name}'` at src/Enlist.Agent/Credentials/AgentEnrollment.cs:82 and "with its credential" at src/Enlist.Agent/Program.cs:192), the `curl.exe` backslash-quoting (correct for Windows native-argument parsing, deliberately so), and the troubleshooting process-match at :233, which matches `dotnet.exe` + `enlist-agent.dll` — the right pattern, and further evidence against Runbook.md:212's `enlist-agent.exe`.

### .demo/README.md

missing | .demo/README.md:59-67 | "### `logs/` — the demo processes' console output" listing only `control-plane.*` and `agent-0N.*` | `start-demo.ps1:158` also writes `portal.log` / `portal.err.log` through the same `Start-Background`, and :170 of that script points the operator at `.demo/logs/portal.log` when the portal does not answer. The opening sentence at :3-4 ("It launches the two demo agents and the control plane") omits the portal for the same reason. Add both.

missing | .demo/README.md:36-50,71-74 | The `agent-0N/` layout (`Packages/`, `Runners/`, `Logs/`) and "delete `.demo/agent-01/` … The agent recreates it" | The DPAPI-protected `credential` file now lives in that data root (start-demo.ps1:21, demo/README.md:167-168) and is not mentioned in either section. Deleting the directory destroys a credential the control plane still considers live, which is why demo/README.md:174 says to revoke the name as well — a reader following the Resetting section alone gets a 409 on the next enrollment.

### deploy/README.md

Clean. `RepoPaths.SampleServiceDir()` and friends do point here (tests/Enlist.TestSupport/RepoPaths.cs:5,8,11), the four application names and their flavours are right, and the launch-profile `--app ../../deploy/<AppName>` claim matches all four samples' launchSettings.json.

### Aware-Architecture-Notes.md

stale | Aware-Architecture-Notes.md:134-136 | "This is precisely the limitation documented in enList's `ManifestEnricher` — `IAppService.Name` is an instance property … **which is why the enrichment pipeline exists at all**" | Present tense about enList **v2**. No `ManifestEnricher` or `IAppService` exists anywhere in this repository; docs/03-architecture/enList-v3-Design.md:217-222 records that "the entire enrichment subsystem is deleted, not ported". v3's contract is already attributes (contracts/EnlistAttributes.cs). Put it in the past tense — the comparison is still worth making, it just describes a problem that has been solved.

stale | Aware-Architecture-Notes.md:294-296 | "**Attributes instead of a shared contract interface.** Removes the shared assembly, and removes the need for enList's whole enrichment pipeline" | Listed under "Directly transferable", but v3 transferred it already: attributes matched by type-name string, no shared binary, which is exactly what lets net472 and net10.0 applications share one contract (Application-Developer-Guide.md:24). §8 should mark which of its six items enList has since adopted, or the list reads as an outstanding to-do.

wrong | Aware-Architecture-Notes.md:304-305 | "closer to what enList **now does with OpenTelemetry**" | enList does not use OpenTelemetry — no reference in any .cs, .csproj or .props file in the repository. Either drop the comparison or say what enList actually does (per-application file logs forwarded to the control plane, with `LogRetention`).

The rest of the file is notes about another codebase and cannot be regression-checked from here; only its enList-facing claims were examined.

## Group 6 - configuration

config | src/Enlist.Runner/Dockerfile:20-21 | `COPY src/Enlist.Runner/ ./Enlist.Runner/` then `RUN dotnet publish Enlist.Runner/Enlist.Runner.csproj -c Release -o /publish` | The root `Directory.Build.props` is never copied into the build context's image layer, and MSBuild walks upward from `/src/Enlist.Runner/` finding nothing — so the runner **inside the image is built as version 1.0.0**, the SDK default, while every runner built on the host is 3.0.0. That directly defeats Directory.Build.props' stated purpose ("One product version for every assembly in the tree … so nothing can drift"), and the drift is invisible because the image is the one copy nobody rebuilds by accident. Add `COPY Directory.Build.props ./` (with the publish path adjusted) or pass `-p:Version=3.0.0`.

config | repository root — no `.dockerignore` | `docker build -f src/Enlist.Runner/Dockerfile -t enlist/runner:dev .` (Dockerfile:12, Container-Developer-Guide.md:252, demo/README.md:70) | The documented build sends the ENTIRE repository root to the engine as build context — every `bin/` and `obj/`, `.demo/`, `deploy/`, and `demo/sql-server/.env`, which holds the SA password in clear text — even though the Dockerfile copies only `src/Enlist.Runner/`. Nothing lands in the image, but the transfer is slow on every build and the password does leave the working tree. Add a `.dockerignore` with at least `**/bin`, `**/obj`, `.demo`, `deploy`, `**/.env`, `.git`.

stale | src/Enlist.Deploy/Enlist.Deploy.csproj:13-16 | "zip a build, upload it to the control plane, and **create-or-update an assignment** for the target **machine(s) or label selector**" | Wrong on both counts. Vocabulary: Agent / Application Policy / Tag. Behaviour: `enlist-deploy` creates no placement at all — its only flags are `--control-plane`, `--app`, `--source`, `--api-key` (src/Enlist.Deploy/Program.cs:11,44,128-131), and Runbook.md:220 says so explicitly ("That CLI takes `--source` and only uploads; it creates no placement").

stale | tests/Enlist.Deploy.Tests/Enlist.Deploy.Tests.csproj:22-25 | "one test proves the full pipeline (enlist-deploy **uploads and assigns** -> a real AgentHost picks it up and runs it) … a tool that uploaded garbage or created a **broken assignment** would fail this" | Same two problems as above; the policy in that test is created by the test, not by the CLI. Test-Plan.md:97 describes `DeployCliTests` correctly ("Zipping a source directory, uploading it, and printing the resulting digest and detected runtime flavor").

config | src/Enlist.ControlPlane/Enlist.ControlPlane.csproj:23 | `<PackageReference Include="System.Reflection.MetadataLoadContext" Version="9.0.9" />` | The only 9.x package in a net10.0 tree where every other Microsoft package is 10.0.x (EF Core 10.0.0, Hosting.WindowsServices 10.0.0, SignalR.Client 10.0.0, ProtectedData 10.0.0). It pulls a 9.x System.* assembly into the control plane's output for no stated reason. Move it to 10.0.0 with the rest, or add a comment saying why it is pinned back.

config | src/Enlist.ControlPlane/appsettings.json:2-4 | `"ControlPlane": "Server=(localdb)\\mssqllocaldb;Database=EnlistControlPlane;…"` in the BASE appsettings | Applies in every environment, including Production, while Deployment-IaC.md:21 says "LocalDB is a development-only substitute, **not a production target**" and §1.4 builds a whole three-identity story on the production database being provisioned separately. A Production control plane whose `ConnectionStrings__ControlPlane` override is forgotten silently aims at LocalDB and fails with the connect error §1.4:72 describes, which reads like a network problem. Move the LocalDB string to `appsettings.Development.json` (where `Authentication:Mode=Off` already correctly lives) and leave the base file without one.

stale | tests/Enlist.TestSupport/Enlist.TestSupport.csproj:11-14 | "referenced by **both** Enlist.ControlPlane.Tests and Enlist.Agent.Tests" | Four projects reference it now — Deploy.Tests:35 and Portal.Tests:34 as well, the latter for `PortalTestServer`, which this comment's list of contents also omits.

style | src/Enlist.ControlPlane/Enlist.ControlPlane.csproj:10 | `Microsoft.AspNetCore.OpenApi" Version="10.0.11"` | The only 10.0.11 in the tree; every other 10.0.x package is 10.0.0. Harmless, but it is the kind of one-off that makes a real version conflict hard to spot later.

style | src/Enlist.Portal/Properties/launchSettings.json:3-23 | The whole `profiles` block indented two extra spaces with the closing brace at column 2 | Structurally valid, just formatted unlike src/Enlist.ControlPlane/Properties/launchSettings.json, which is otherwise its twin.

Verified correct and left alone across this group:

- **Directory.Build.props** — `<Version>3.0.0</Version>`, one place, and its claim that `/health` strips the SourceLink `+commit` suffix matches src/Enlist.ControlPlane/Program.cs:154-160.
- **enList_v3.slnx** — lists all 17 `.csproj` in the tree, none missing, none stale; `contracts/` correctly has no project (it is a `.props` source import).
- **The authentication defaults.** `Authentication:Mode` appears ONLY in the two `appsettings.Development.json` files, both as `Off`, and both projects' launch profiles bind loopback only (`http://localhost:5293` / `https://localhost:7216;http://localhost:5293`, and 5231/7203 for the portal) — so the two startup rules are satisfied in development, and a Production run with no override defaults to `Required`, which is the safe direction. No launch profile would refuse to start.
- **The portal's missing `ControlPlane:BaseUrl` in base appsettings.json** is deliberate and documented (Program.cs:59-60 throws by design; Deployment-IaC.md:46).
- **Test package versions are uniform** across all five test projects — coverlet.collector 6.0.4, Microsoft.NET.Test.Sdk 17.14.1, xunit 2.9.3, xunit.runner.visualstudio 3.1.4, Xunit.SkippableFact 1.5.23 (in the two projects that skip). Cronos 0.11.0 matches between Enlist.Agent and Enlist.ControlPlane.Contracts.
- **Enlist.Runner's zero-PackageReference guard** (`EnforceZeroPackageReferences`, an `Error` target on Build and Restore) is real and still empty-handed, which is what the Dockerfile's "needs no restore" comment depends on; Enlist.Runner.Legacy's two-package exception is argued in place.
- **The four sample csproj files** are consistent with each other and with deploy/README.md (`OutputPath` → `deploy/<App>`, `AppendTargetFrameworkToOutputPath=false`, contract imported as source; Legacy alone on net472).
- **.gitignore** — per the review brief, coverage confirmed correct and not re-examined.

## Summary

**Files read line by line (18):** demo/start-demo.ps1, demo/demo-status.ps1, demo/sql-server/demo-db.ps1; docs/05-operations/{Runbook,Test-Plan,Deployment-IaC,Installer-UI-Design,Demo-Install-Design}.md; docs/02-building-applications/{Application-Developer-Guide,Container-Developer-Guide}.md; docs/06-background/Aware-Architecture-Notes.md; demo/README.md, demo/sql-server/README.md, deploy/README.md, .demo/README.md; .gitignore, Directory.Build.props, enList_v3.slnx, all 17 csproj files, all four appsettings and two launchSettings under src/, src/Enlist.Runner/Dockerfile. Claims were checked against src/ and tests/ throughout — roughly forty separate code lookups, every one of which is cited above.

**In good shape.** The three demo scripts are genuinely well built: the PowerShell 5.1 traps are not just avoided but documented where they were hit, every quoting site is correct, and the process matching really does require both the DLL and a demo-specific argument. Every numeric default in Deployment-IaC §1.3 is right. The authentication story (Deployment-IaC §1.8, Runbook §2.7/§3.19/§3.20, demo/README's enrolment section, the demo's own `Authentication__Mode=Off`-on-loopback) is accurate and consistent across five documents and the code — the C2 rewrite landed cleanly almost everywhere. Application-Developer-Guide is the cleanest file in scope: one wrong line in 421. Test-Plan's per-project counts, §5 gap list and §2a postmortem all hold up. The config tree is uniform and the launch/appsettings defaults sit on the safe side of both listener rules.

**Top three fixes.**

1. **The examples that cannot start.** Deployment-IaC §4.1/§4.2, Installer-UI-Design's `http://+:5293` / `http://+:5231` page defaults and silent-install example, and Container-Developer-Guide:152's `--urls http://0.0.0.0:5293` all describe control planes and portals that today refuse to start under the two listener rules. These are the instructions someone will actually copy.
2. **Runbook §3.13's `enlist-agent.exe` process match, and §4's position.** The duplicate-agent recipe returns nothing in the setup §3.6 describes on the same page, and the escalation table is stranded in the middle of section 3 with no rows for the authentication-era entries below it. Both are cheap, and the duplicate-agent case is the one that silently corrupts reports.
3. **The stale test and schema numbers.** "121 tests" in two places (Deployment-IaC:281, Container-Developer-Guide:574), "800/800" in Test-Plan:131, "five tables / three migrations" in demo/sql-server/README.md:162,164, and the migration-collapse narrative at Deployment-IaC:90 alongside the already-known :73. Each is a one-line edit, and each is a number a reader would otherwise trust.
