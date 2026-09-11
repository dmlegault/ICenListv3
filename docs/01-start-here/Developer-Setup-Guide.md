# Developer Setup & Onboarding Guide

**Product:** enList v3
**Document status:** Derived from the current implementation and verified session commands as of 2026-09-03.

---

## 1. Prerequisites

| Requirement | Notes |
|---|---|
| .NET 10 SDK | All five components (`Enlist.Portal`, `Enlist.ControlPlane`, `Enlist.Agent`, `Enlist.Runner`, `Enlist.Deploy`) target `net10.0`. |
| SQL Server or LocalDB | The control plane's default connection string points at `(localdb)\mssqllocaldb`; any SQL Server instance works if you override `ConnectionStrings:ControlPlane`. For a **demo** you usually want data that persists and is visible in SSMS — [`demo/sql-server/`](../../demo/sql-server/README.md) provides SQL Server in a WSL container (`wslc`, no Docker) for exactly that. The test suite always uses LocalDB regardless. |
| Windows | The agent's Windows Service hosting and Job Object process-tree cleanup are Windows-specific; the sample plugins and reference workflow assume a Windows managed machine. |
| A browser | For Enlist.Portal (Blazor Server, MudBlazor UI). |

## 2. Clone and Restore

```bash
git clone <repo-url> enList_v3
cd enList_v3
dotnet build enList_v3.slnx
```

This restores and builds every project in the solution: `src/` (5 components), `samples/` (reference plugin apps), `tests/` (4 test projects + shared test support), `contracts/` (source-only plugin attribute file, pulled in via `.props` import, not built standalone).

## 3. Run the Control Plane

```bash
dotnet run --project src/Enlist.ControlPlane/Enlist.ControlPlane.csproj --urls http://localhost:5293
```

- On first run, EF Core migrations apply automatically against the LocalDB database `EnlistControlPlane` — **but only because this is the `Development` environment**. Outside Development the control plane verifies the schema and refuses to start rather than creating or altering anything ([`Deployment-IaC.md` §1.4](../05-operations/Deployment-IaC.md)), so a `Production` run against an absent database fails by design instead of silently creating one.
- Package blobs are written under `PackageBlobs/` relative to the control plane's content root by default (`PackageStorage:Root` config key to override).
- Confirm it's up: `curl http://localhost:5293/api/agents` should return `[]` on a fresh database.
- **Demoing rather than developing?** LocalDB is right for day-to-day work and for the test suite, but a demo wants a database that is still there tomorrow and that an audience can watch you query. [`demo/sql-server/`](../../demo/sql-server/README.md) runs one in a WSL container (`wslc`, no Docker needed); point this same command at it by setting `ConnectionStrings__ControlPlane` before launching, leaving `ASPNETCORE_ENVIRONMENT` as `Development` so the schema is still created for you on first connect. The standard demo fleet (control plane on :5293, two agents from `.demo/`) is started with [`demo/start-demo.ps1`](../../demo/start-demo.ps1), which does that wiring for you.

## 4. Run the Portal

> **⚠️ Launch method matters.** Always start the portal with `dotnet run --project ...`, **never** `dotnet <path-to-compiled-dll>`. Launching the compiled DLL directly resolves the ASP.NET Core content root to the current working directory rather than the project's own folder, which silently breaks `MapStaticAssets()` — every static asset (MudBlazor's CSS/JS, the app's own logo, `blazor.web.js`) returns either an empty 200 response or throws `FileNotFoundException` from `StaticAssetDevelopmentRuntimeHandler`, depending on exactly how wrong the content root is. This is not obvious from the browser (the page loads with no visible error, just unstyled/broken) and cost significant debugging time to root-cause — see [`Runbook.md`](../05-operations/Runbook.md) for the full incident writeup.

```bash
ControlPlane__BaseUrl=http://localhost:5293 dotnet run --project src/Enlist.Portal/Enlist.Portal.csproj --urls http://localhost:5231
```

(`ControlPlane__BaseUrl` is the double-underscore environment-variable form of `appsettings.json`'s `ControlPlane:BaseUrl` — already set to `http://localhost:5293` in `appsettings.Development.json`, so the environment variable is only needed if you're running against a non-default control plane URL or a non-Development environment.)

Open `http://localhost:5231` — you should see the Applications page (empty, with an info alert, on a fresh system) under the "enList Portal" dark theme.

## 5. Run an Agent

An agent needs `enlist-runner`'s build output as its `--runner-bin` (build it once — it doesn't need to be running):

```bash
dotnet build src/Enlist.Runner/Enlist.Runner.csproj
```

Then, for each agent identity you want to simulate (you can run several agent processes on one physical box during development, each with a distinct `--agent` name and `--data` directory):

```bash
dotnet src/Enlist.Agent/bin/Debug/net10.0/enlist-agent.dll \
  --control-plane http://localhost:5293 \
  --agent DEV-AGENT-01 \
  --runner-bin src/Enlist.Runner/bin/Debug/net10.0 \
  --data /path/to/some/writable/scratch/dir
```

- `--agent` defaults to the machine name if omitted. **Run exactly one process per agent name** — two processes sharing a name both heartbeat and both post status, so the control plane's latest report alternates between them and the loser reports applications it cannot run as `Failed`. It looks exactly like stale portal data.
- `--container-image <image>` enables container isolation on this agent; without it the agent reports `process` as its only supported isolation mode. `--container-engine wslc` drives WSL containers (`wslc`, WSL 2.9.11+) instead of Docker Desktop — see the container guide §2a.
- `--data` defaults to `<agent exe dir>/Data` if omitted — pass an explicit, writable path when running multiple simulated agents side by side (each needs its own `Runners/`, `Logs/`, `Packages/` subtree).
- `--legacy-runner-bin <path>` is optional and lets this same agent also host `net472`-flavored applications alongside modern ones (the flavor is detected server-side from the uploaded package, not declared on the command line) — see [`API-Specification.md` §7.1](../03-architecture/API-Specification.md#71-runtimeflavor). Point it at `src/Enlist.Runner.Legacy/bin/Debug/net472` (build that project first: `dotnet build src/Enlist.Runner.Legacy`); a sample net472 application (`samples/Enlist.Sample.Legacy`, deployed to `deploy/LegacySample`) exercises it end to end. An assignment declaring `net472` on an agent without `--legacy-runner-bin` configured is marked `Failed` with a clear reason rather than silently mishandled — see [`SAD.md` §9](../03-architecture/SAD.md#9-design-vs-implementation) for the one known limitation (no per-application `AppDomain` isolation between co-hosted net472 applications).
- On startup you should see `enlist-agent starting: ...` and, moments later, that agent appear in `GET /api/agents`.
- Running interactively (not as a Windows Service), `Ctrl+C` triggers the same graceful `AgentHost.StopAsync` the Service Control Manager would.

**Installing as a real Windows Service** (production-style, not needed for local development):

```powershell
sc.exe create enlist-agent binPath= "C:\path\to\enlist-agent.exe --control-plane https://your-control-plane --runner-bin C:\path\to\runner-bin"
```

## 6. Build and Deploy a Sample Application

Four reference plugin apps exist under `samples/`, each building straight into `deploy/<AppName>/` (see each project's `OutputPath`):

| Sample | Target | Deploys to | Contents |
|---|---|---|---|
| `Enlist.Sample.Service` | net10.0 | `deploy/SampleService` | 1 service, 1 job — also the only one shipping a `*.settings.json` |
| `Enlist.Sample.OrderProcessor` | net10.0 | `deploy/OrderProcessor` | 4 services, 5 jobs — including **Ledger Rebuild**, the one sample with a long-running job that honors its CancellationToken |
| `Enlist.Sample.DataPipeline` | net10.0 | `deploy/DataPipeline` | 5 services, 3 jobs |
| `Enlist.Sample.Legacy` | **net472** | `deploy/LegacySample` | 1 service, 2 jobs — exercises the legacy runner end to end (§5), including job cancellation under net472 |

```bash
dotnet build samples/Enlist.Sample.Service/Enlist.Sample.Service.csproj
dotnet build src/Enlist.Deploy/Enlist.Deploy.csproj

dotnet src/Enlist.Deploy/bin/Debug/net10.0/enlist-deploy.dll \
  --control-plane http://localhost:5293 \
  --app SampleService \
  --source deploy/SampleService
```

**`enlist-deploy` uploads a package and nothing else** — those three flags are its entire surface. It does not create a placement, choose agents, or set a desired state, and the runtime flavor is detected server-side from the uploaded build rather than declared on the command line.

> Earlier revisions of this guide showed `--machines`, `--tags`, `--desired-state` and `--runtime` here. Those flags no longer exist; deciding *where* an application runs is the portal's job, through an Application Policy rule. The example above is the current, complete invocation.

Deciding where it runs is a separate step: open the portal's **Applications** tab, expand the application, and add a policy rule targeting an agent by tag (every agent implicitly carries `agent=<name>`, so "just this one agent" is simply the most specific possible tag rule). Within moments the application should appear as `Running` in the expanded panel, with its services and jobs listed per agent.

## 7. Writing a New Plugin (Service/Job)

> **You do not need any of §3–§6 to write or debug an application.** The runner can run one standalone — no control plane, portal, agent or database — and a launch profile makes F5 work directly from your own project. See **[`Application-Developer-Guide.md`](../02-building-applications/Application-Developer-Guide.md)**, which covers the whole authoring loop in detail. The short version:
>
> ```bash
> dotnet build src/Enlist.Runner/Enlist.Runner.csproj    # once
>
> # What would enList discover in this build output? (exit 1 = nothing)
> src/Enlist.Runner/bin/Debug/net10.0/enlist-runner.exe --check --app deploy/SampleService
>
> # Run it standalone: auto-starts services, then takes list/start/stop/run/quit
> src/Enlist.Runner/bin/Debug/net10.0/enlist-runner.exe --dev --app deploy/SampleService
>
> # ...or with log output in its own console window, leaving this one as a clean prompt
> src/Enlist.Runner/bin/Debug/net10.0/enlist-runner.exe --dev --log-window --app deploy/SampleService
> ```
>
> Each sample carries a `Properties/launchSettings.json` with **enList dev host (log window)** (the F5 default), **enList dev host (single window)** and **enList check** profiles — set the sample as the startup project and press F5 to land on a breakpoint in `[EnlistStart]`. For net472, use `src/Enlist.Runner.Legacy/bin/Debug/net472/enlist-runner.exe`.
>
> The dev host deliberately does not reproduce agent behaviour — cron never fires, nothing restarts on failure, logs do not reach the portal. [`Application-Developer-Guide.md` §8](../02-building-applications/Application-Developer-Guide.md) lists exactly what is and isn't covered.

1. Create a new class library targeting `net10.0`.
2. Add `<Import Project="..\..\contracts\Enlist.Contracts.props" />` to the `.csproj` — **do not** add a `PackageReference`/`ProjectReference` to any compiled "Enlist.Contracts"-style assembly; the attribute contract is distributed as source specifically so a plugin never needs to match the runner's own binary version (see [`SAD.md` §2.6](../03-architecture/SAD.md#3-components) and [`LLD.md` §6](../03-architecture/LLD.md#6-runner-plugin-discovery--attribute-matching-by-name)).
3. Author a service:

   ```csharp
   [EnlistService("My Service", Description = "What it does.")]
   public sealed class MyService
   {
       [EnlistStart]
       public Task Start(CancellationToken stopping, Action<string> log) { /* ... */ return Task.CompletedTask; }

       [EnlistStop]
       public void Stop() { /* optional */ }
   }
   ```

4. Author a job:

   ```csharp
   [EnlistJob("My Job", Description = "What it does.", Cron = "0 0 * * * ?")]
   public sealed class MyJob
   {
       [EnlistExecute]
       public void Run(CancellationToken cancelling, IDictionary<string, string> settings) { /* ... */ }
   }
   ```

5. Optionally declare an application-level description once per project (e.g. in an `AssemblyInfo.cs`):

   ```csharp
   [assembly: EnlistApplication(Description = "What this whole application does.")]
   ```

6. A settings file at `<AssemblyName>.settings.json` (or `<TypeFullName>.settings.json` for a per-type override), copied to the output directory, is optional and flattens into the `IDictionary<string,string> settings` parameter your methods can bind.
7. Verify what enList will actually see, before deploying anything:

   ```bash
   src/Enlist.Runner/bin/Debug/net10.0/enlist-runner.exe --check --app deploy/MyApp
   ```

   Exit code `1` means nothing was discovered — the failure that otherwise shows up as an application reporting `Running` with nothing in it.

8. Build, then upload with `enlist-deploy` as in §6, and add a policy rule in the portal to place it.

## 8. Running the Test Suite

```bash
dotnet test enList_v3.slnx
```

Runs all five test projects (`Enlist.ControlPlane.Tests`, `Enlist.Agent.Tests`, `Enlist.Runner.Tests`, `Enlist.Deploy.Tests`, `Enlist.Portal.Tests`) — see [`Test-Plan.md`](../05-operations/Test-Plan.md) for what each covers. As of this writing: 205 tests, all passing (50 Runner, 3 Deploy, 68 ControlPlane, 66 Agent, 18 Portal). The 16 container tests skip themselves when their engine (Docker or wslc) or its runner image is unavailable — see Container-Developer-Guide.md &sect;9.

## 9. Common Pitfalls

| Symptom | Cause | Fix |
|---|---|---|
| An application reports `Running` but does nothing, and nothing is in the portal | Discovery found no services or jobs — a non-public class, a missing `[EnlistExecute]`, or an assembly that failed to load | Run `enlist-runner --check --app <dir>` (§7); it names the cause and exits `1`. This is the failure mode with no other symptom. |
| A whole application's assemblies are skipped with `is not an absolute path` | A relative `--app` path — the assembly load context requires a rooted one | Fixed: both runners now normalise via `Path.GetFullPath`. The agent always passed absolute paths, so this only ever bit `--check`/`--dev` from a shell. |
| Two agent processes with the same `--agent` name | Started twice; both heartbeat and both POST status, so `report/latest` alternates between them | The loser cannot run the applications the winner owns, so it reports them `Failed` with a climbing restart count — which looks exactly like stale portal data. Expect one `enlist-agent.exe` per name (`dotnet run` shows a launcher + child pair, so N agents look like 2N rows). |
| Portal loads with no styling / broken images, no visible error | Launched via `dotnet <dll>` instead of `dotnet run --project` | See the warning in §4. |
| Build fails with "file is locked by .NET Host (PID)" | A previous `dotnet run`/`dotnet <dll>` instance of the same component is still running and holding its own output DLL open | Stop the running process (`Stop-Process -Id <PID>`) before rebuilding — the running agent/portal/control-plane process must be stopped, not just the build re-attempted. |
| An agent reachable only via a tag selector shows "no rules" in one view but the correct count in another | Two independent tag-matching implementations drifted (fixed — see [`SAD.md` §5](../03-architecture/SAD.md#5-cross-cutting-design-decisions)); if this recurs, check that both `/api/application-policies?machineName=` and `/api/agents/{name}/policies` still route through `ResolveEffectivePoliciesForAgentAsync`. |
| A machine stays "Stale" indefinitely with nothing obviously wrong | Confirm the agent's heartbeat loop is actually running (`AgentHostOptions.HeartbeatInterval`, default 2 min) — a report age past 5 minutes with a genuinely healthy, idle agent should self-correct within one heartbeat interval. |
| A policy change made right after a control-plane restart never reaches the agent | The agent's SignalR connection reconnected but didn't re-join its machine group — confirmed fixed in `ControlPlaneAssignmentSource`'s `Reconnected` handler; if this regresses, verify `JoinAgentGroupMethod` is re-invoked on every reconnect, not only at initial connect. |
