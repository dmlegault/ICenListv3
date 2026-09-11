# Deployment & Infrastructure-as-Code (IaC) Specification

**Product:** enList v3
**Document status:** Derived from the current implementation. Originally 2026-09-03; revised 2026-09-08 — §1.4 (migrations are Development-only now), §1.6 (Windows Service / IIS hosting) and §1.7 (containerized applications, previously undocumented here) were added or rewritten, and the header's "no Dockerfile anywhere" claim was corrected.

> **Important:** deployment of the enList *platform itself* is manual — no compose file, no Terraform/Bicep/ARM, no CI/CD definitions (there is no `.github/` directory) — using the commands in [`Developer-Setup-Guide.md`](../01-start-here/Developer-Setup-Guide.md) or the service/IIS hosting in §1.6.
>
> **One Dockerfile does exist and is load-bearing: [`src/Enlist.Runner/Dockerfile`](../../src/Enlist.Runner/Dockerfile).** That is not platform IaC — it builds the `enlist/runner` image an agent uses to run a *managed application* in a container (§1.7). Containerized applications are a shipped feature, not a proposal, and that image is a real deployment prerequisite on any agent expected to run one.
>
> **Section 1** documents the actual current state; **Section 4** proposes a starting IaC shape for the platform tiers and is explicitly marked as not yet implemented.

---

## 1. Current Deployment Reality

### 1.1 Components to place

| Component | Cardinality | Runs where |
|---|---|---|
| Enlist.ControlPlane | 1 per environment | A host with outbound+inbound network access reachable by every managed machine and by Enlist.Portal. Runs as a Windows Service, under IIS, or in a container — see §1.6. |
| SQL Server database | 1 per environment | Reachable from the control plane host. LocalDB is a development-only substitute, not a production target. |
| Package blob storage | 1 per environment, colocated with or reachable from the control plane | Filesystem-backed (`PackageBlobStore`); `PackageStorage:Root` config key. No blob-storage-service integration currently exists (S3/Azure Blob, etc.) despite the code comment referencing that as a design possibility ("Package blobs on the filesystem or blob storage, keyed by digest"). |
| Enlist.Portal | 1 per environment | A host reachable by operators' browsers, with outbound access to the control plane. Runs as a Windows Service, under IIS, or in a container — see §1.6 (IIS needs two app-pool changes for Blazor Server). |
| Enlist.Agent | **1 per managed machine** | Every machine an application should be able to run on — as a Windows Service in production, per `Program.cs`'s `AddWindowsService` wiring. |
| Enlist.Runner build output | Not a standing service | Staged per-application-instance by each agent from its `--runner-bin` folder; that folder itself must be present on every managed machine (copied there once, e.g. as part of agent installation). |
| `enlist-deploy` | Not a standing service | Run on demand from an operator's machine or a CI runner — needs only outbound HTTPS/HTTP to the control plane. |
| `enlist/runner` container image | Not a standing service | Required **only** on agents expected to run containerized applications, alongside a working container engine — see §1.7. Built from [`src/Enlist.Runner/Dockerfile`](../../src/Enlist.Runner/Dockerfile). |

### 1.2 Network Posture

- **Managed machines (agents) never accept an inbound connection for enList's own purposes** — this is the system's defining network-security property (see [`SAD.md` §1](../03-architecture/SAD.md#1-architectural-goals)). Firewall rules for managed machines only need to permit **outbound** traffic to the control plane's host/port.
- The control plane host needs an **inbound** rule for its API/SignalR port (`5293` in development), reachable from every managed machine and from the portal.
- The portal host needs an inbound rule for its own port (`5231` in development), reachable from operators' browsers, and outbound access to the control plane.
- What arrives on the control plane's inbound port must be authenticated, and off loopback that means TLS — §1.8. The port being reachable is a routing fact, not a trust decision.

### 1.3 Configuration Surface

| Component | Key setting | Source |
|---|---|---|
| Enlist.ControlPlane | `ConnectionStrings:ControlPlane` | `appsettings.json` / `appsettings.{Environment}.json` / environment variable `ConnectionStrings__ControlPlane` |
| Enlist.ControlPlane | `PackageStorage:Root` | Same, defaults to `<content root>/PackageBlobs` |
| Enlist.ControlPlane | `PackageStorage:MaxUploadBytes` | Same, defaults to 536870912 (512 MB) — the upload endpoint's body limit; the portal's picker allows 500 MB |
| Enlist.ControlPlane | `PackageRetention:RetentionPeriod` / `:SweepInterval` | Same, defaults 30 days / 6 hours |
| Enlist.ControlPlane | `LogRetention:RetentionPeriod` / `:SweepInterval` | Same, defaults 3 days / 1 hour |
| Enlist.ControlPlane | `ReportRetention:RetentionPeriod` / `:SweepInterval` | Same, defaults 1 day / 1 hour — status reports older than this are deleted, except each agent's newest |
| Enlist.Portal | `ControlPlane:BaseUrl` | `appsettings.json` / environment variable `ControlPlane__BaseUrl` — **required**, the app throws at startup if absent |
| Enlist.Agent | `--control-plane`, `--agent`, `--runner-bin`, `--data`, `--legacy-runner-bin`, `--container-image`, `--container-engine` (`docker`, the default, or `wslc`) | Command-line arguments only (this component reads no `appsettings.json`) |
| Enlist.ControlPlane / Enlist.Portal | `ASPNETCORE_URLS` (or `--urls`) | Required when running as a Windows Service — a service has no shell to inherit a binding from (§1.6). |
| Enlist.ControlPlane / Enlist.Portal | `ASPNETCORE_ENVIRONMENT` | Machine-level environment variable; a service does NOT inherit your user variables. Unset means `Production`, which activates the migration verification in §1.4. |

### 1.4 Database Migrations

**The control plane auto-migrates in `Development` only.** Outside it, the process verifies the schema and refuses to start if it is wrong — it never issues DDL.

This split is about database **permissions**, not tidiness. `Database.MigrateAsync()` does two things a production SQL login is normally not granted:

1. **Creates the database** when it is absent — that needs `dbcreator`, which an application login does not have.
2. **Applies DDL** (`CREATE`/`ALTER TABLE`) on every start — an app login is typically `db_datareader` + `db_datawriter` + `EXECUTE` and nothing more.

Left unconditional, the first production start fails on a permissions error that reads like a bad connection string.

So production splits the work across three identities:

| Who | Does |
|---|---|
| DBA / provisioning | Creates an **empty** database and grants the app login read/write |
| Deploy pipeline | Applies schema with an **elevated** account, as its own step before new instances start |
| Control plane process | Runs read/write only. Verifies, never modifies. |

**What the process does at startup outside Development** (`Program.cs`, immediately after `builder.Build()`):

- Cannot connect → fails with *"Cannot connect to the control plane database. Outside Development this process never creates it…"*
- Connects, but migrations are pending → fails naming them: *"The control plane database is missing 1 migration(s): 20260908171028_InitialCreate. Apply them as a deploy step with an account that has DDL rights…"*

Both are checked separately and on purpose — the two failures have completely different fixes, and a missing database would otherwise surface as an opaque connection exception from the pending-migrations query.

Failing to start is deliberate. A control plane serving against a stale schema is worse than one that refuses: the damage appears later as scattered runtime errors on whichever endpoint first touches a missing column, far from the deploy that caused it.

**Applying schema in the pipeline.** Prefer a migration bundle over a script — it is a self-contained executable and needs no .NET SDK or SQL tooling on the deploy agent:

```bash
dotnet ef migrations bundle --project src/Enlist.ControlPlane/Enlist.ControlPlane.csproj --self-contained -o out/migrate-controlplane.exe

# on the deploy agent, with an account that has DDL rights:
out/migrate-controlplane.exe --connection "Server=…;Database=EnlistControlPlane;…"
```

`dotnet ef database update` or `dotnet ef migrations script --idempotent` work too, if you would rather hand SQL to a DBA for review.

The schema is currently a **single** `InitialCreate` migration — the three that had accumulated during development were collapsed into one once it was established that no existing database needed preserving, so a first install is one step rather than a replay.

### 1.5 Build Artifacts per Component

| Component | Publish command (proposed — no publish profile exists yet) |
|---|---|
| Enlist.ControlPlane | `dotnet publish src/Enlist.ControlPlane/Enlist.ControlPlane.csproj -c Release -o out/controlplane` |
| Enlist.Portal | `dotnet publish src/Enlist.Portal/Enlist.Portal.csproj -c Release -o out/portal` |
| Enlist.Agent | `dotnet publish src/Enlist.Agent/Enlist.Agent.csproj -c Release -o out/agent` |
| Enlist.Runner | `dotnet publish src/Enlist.Runner/Enlist.Runner.csproj -c Release -o out/runner-bin` — this output folder is what every agent's `--runner-bin` must point at. |
| Enlist.Deploy | `dotnet publish src/Enlist.Deploy/Enlist.Deploy.csproj -c Release -o out/deploy-cli` |
| Schema migration bundle | `dotnet ef migrations bundle --project src/Enlist.ControlPlane/Enlist.ControlPlane.csproj --self-contained -o out/migrate-controlplane.exe` — run by the deploy pipeline with a DDL-capable account BEFORE starting new control-plane instances (§1.4). |

### 1.6 Hosting Modes (Windows, outside containers)

The control plane and the portal are **hosting-mode neutral**: the same published output runs under IIS, as a Windows Service, or from `dotnet run`. Nothing is rebuilt to switch, and no configuration selects the mode — each host detects it.

`Program.cs` in both calls `builder.Services.AddWindowsService(...)`, which is inert unless the process was actually started by the SCM (`WindowsServiceHelpers.IsWindowsService()` checks that the parent process is `services.exe`, so it is false under IIS, false for `dotnet run`, and false on non-Windows).

> **The content-root trap.** Both hosts pass `ContentRootPath` in `WebApplicationOptions` rather than relying on the default, because `WebApplicationBuilder` resolves the content root **during** `CreateBuilder` from the current working directory — and the SCM starts a service with its working directory set to `%SystemRoot%\System32`. Setting it afterwards is not possible either: `ConfigureHostBuilder.UseContentRoot` throws `NotSupportedException`, which is why `UseWindowsService()` cannot be used on a web host at all.
>
> For the portal this is not hypothetical. A wrong content root silently breaks `MapStaticAssets()` — assets return empty 200s and the page loads unstyled with **no error anywhere**. That exact failure is written up in [`Runbook.md`](Runbook.md) from a `dotnet <dll>` launch; a service installed without this handling reproduces it identically.
>
> **Verified under the SCM**, not just reasoned about: `enlist-portal` installed with `binPath= "C:\enlist\portal\Enlist.Portal.exe --urls http://+:5231"` came up fully styled. Its process was confirmed to be parented by `services.exe` — the precise condition `IsWindowsService()` tests — so the branch that sets `ContentRootPath` is the one that ran.

#### As a Windows Service

```powershell
# elevated PowerShell. Note sc.exe's syntax: a space is required AFTER each '='.
sc.exe create enlist-controlplane `
  binPath= "C:\enlist\controlplane\Enlist.ControlPlane.exe --urls https://+:5293" `
  start= auto
sc.exe description enlist-controlplane "enList control plane"

sc.exe create enlist-portal `
  binPath= "C:\enlist\portal\Enlist.Portal.exe --urls https://+:5231" `
  start= auto depend= enlist-controlplane
sc.exe description enlist-portal "enList portal"

# restart on failure rather than staying down
sc.exe failure enlist-controlplane reset= 86400 actions= restart/5000/restart/5000/restart/60000
sc.exe failure enlist-portal        reset= 86400 actions= restart/5000/restart/5000/restart/60000

sc.exe start enlist-controlplane
sc.exe start enlist-portal
```

- **`https://`** in both `binPath`s is not optional: with authentication on (the default), a listener off loopback that is plain HTTP refuses to start (§1.8). The certificate comes from `Kestrel:Certificates:Default` in `appsettings.json`.
- **Binding** — a service gets no shell, so `--urls` in `binPath` is how it learns what to listen on (`Args` are passed through to the host). `ASPNETCORE_URLS` as a machine-level environment variable works equally well.
- **Environment** — a service inherits **machine** environment variables, not your user ones. `ASPNETCORE_ENVIRONMENT` unset means `Production`, which is the intent here — but that is also what activates the migration verification in §1.4, so the database must already exist and be current.
- **`depend=`** only orders startup on one machine; it is not a health check. The portal throws at startup if `ControlPlane:BaseUrl` is unset, so rely on the failure/restart actions above for boot races rather than on ordering. For an actual health check, the control plane answers `GET /health` — 200 with its version when the database is reachable, 503 when it is not — which is what a service monitor or load balancer should probe ([API-Specification §5b](../03-architecture/API-Specification.md)).
- **Service account** — needs read on the install directory, write on `PackageStorage:Root` (control plane), and a SQL login. `LocalSystem` works but is more privilege than this needs; a dedicated service account with a SQL login mapped to `db_datareader`/`db_datawriter` matches §1.4's least-privilege split.
- **Logging** — `AddWindowsService()` routes logging to the Windows Event Log when running as a service. That is the only place a startup failure can surface: a bad connection string, a missing migration, or an unset `ControlPlane:BaseUrl` all appear there and nowhere else.

#### Under IIS

Install the **ASP.NET Core Hosting Bundle** on the host; `dotnet publish` already emits the `web.config` the ASP.NET Core Module needs. Application pool: **No Managed Code**, and set the pool identity to an account with the same rights as above.

**For the portal, two app-pool defaults must be changed**, or users are dropped mid-session for no visible reason. Blazor Server holds per-user circuit state in memory over a SignalR WebSocket, so process lifetime is directly user-visible:

| Setting | Default | Set to |
|---|---|---|
| Idle Time-out (minutes) | 20 | **0** |
| Regular Time Interval (minutes) | 1740 | **0** |

If you ever run more than one portal instance you also need **sticky sessions** — a Blazor Server circuit cannot move between instances.

The control plane has no such constraint: it holds no per-user state, and an agent's SignalR client reconnects on its own (see `ControlPlaneAssignmentSource`'s `Reconnected` handler).

#### Which to choose

| | Windows Service | IIS |
|---|---|---|
| Control plane | **Recommended** — no per-user state, matches the agent's deployment story, one instance keeps §1.4's migration checks simple | Fine; you gain cert management |
| Portal | Works, but you own TLS | **Recommended** — TLS/cert management and port 443, provided the two pool settings above are changed |

Note the portal's own code comment already assumed IIS (*"since this runs under IIS/a web host rather than being launched with arguments"*), which is why its configuration comes from `appsettings`/environment rather than command-line flags like the agent's.

### 1.7 Agents That Run Containerized Applications

Isolation is a property of a **policy rule**, not of a package — the same digest runs as a process on one agent and in a container on another, with no rebuild. That makes the container engine a **per-agent** deployment prerequisite, not a platform-wide one, and it is entirely optional: an agent without one runs everything as a process exactly as before.

An agent expected to run containerized applications needs three things:

| Prerequisite | Notes |
|---|---|
| A working container engine | Docker today. The agent probes it (`docker version` against the **server**) on every heartbeat and reports the result. |
| The `enlist/runner` image, locally available | Built from [`src/Enlist.Runner/Dockerfile`](../../src/Enlist.Runner/Dockerfile). **Rebuild it after any runner change** — the image carries a copy of the runner. |
| `--container-image <image>` on the agent command line | Without it the agent reports `process` as its only supported isolation mode, and a container rule targeting it will not start. |

```bash
docker build -t enlist/runner:dev -f src/Enlist.Runner/Dockerfile .
```

**The capability the control plane records is a probe result, not a flag.** Passing `--container-image` proves someone typed it; it does not prove an engine is installed, running, or reachable. The Agents tab's **containers** chip reflects the probe, so an agent configured for containers whose Docker daemon is down does not falsely advertise the capability. See [`Container-Developer-Guide.md`](../02-building-applications/Container-Developer-Guide.md) for building, running and debugging the image.

**Two constraints worth knowing before planning a rollout:**

- **`net472` applications cannot be containerized.** The image is Linux; a .NET Framework application cannot run in it. The control plane rejects such a rule at write time rather than letting it fail on the agent.
- **A container outlives an abnormally-killed agent.** A locally-spawned runner is enrolled in a Windows Job Object, so a dead agent takes its runners with it; nothing equivalent ties a container's lifetime to the agent's. Each agent therefore reaps containers bearing **its own** `enlist.agent` label at startup — scoped by label, never by name prefix, because several agents commonly share one engine. See [`Runbook.md` §3.9](Runbook.md).

Ports declared on a rule are published by the engine, and the resolved host port is reported back — `GET /api/endpoints` and `/api/endpoints/traefik` expose that for a reverse proxy ([`API-Specification.md` §5a](../03-architecture/API-Specification.md)). Prefer a **dynamic** host port: a hard-coded one cannot serve a rule that matches several agents.

---

### 1.8 Authentication

All three steps of [`Authentication-Design.md`](../03-architecture/Authentication-Design.md) are in place (2026-09-11): the control plane authenticates every caller, agents enroll with a join token, the portal signs people in with Windows and holds its own key, and `enlist-deploy` takes an API key. What an operator needs to know:

- **`Authentication:Mode`** is `Required` unless configuration says `Off`, and `Off` is honoured **only when every listener is bound to loopback** — `localhost`, `127.0.0.1` or `::1`. Bind an `Off` control plane to `+`, `*`, `0.0.0.0` or a host address and it refuses to start, with the reason in the Event Log. There is no override. The trusted-network assumption the requirements carried until now ([BRD §7](../04-requirements/BRD.md)) is no longer something a deployment can rely on by omission: an unauthenticated control plane reachable from a network cannot be configured.
- Under **`Required`**, a listener off loopback must be `https://` (bearer tokens are never sent in the clear); plain `http://` off loopback refuses to start. TLS terminates at Kestrel: `Kestrel:Certificates:Default` with a certificate from the machine store (`Store`/`Location`/`Subject` or `Thumbprint`) or a PFX, and `--urls https://+:5293` in the service's `binPath`.
- **Credentials are created out of band**, on the control plane host, with the verbs on the executable (`create-api-key`, `create-join-token`, `revoke-api-key`, `revoke-agent`, `list-keys`, `list-join-tokens` — [API-Specification §0](../03-architecture/API-Specification.md)). They use `ConnectionStrings:ControlPlane` exactly as the server does. Each prints its secret once. Day to day the portal's *Access* page and *Enroll agent* do the same through the Operator-only endpoints ([API-Specification §0](../03-architecture/API-Specification.md)).
- **Agents enroll once.** Start a new agent once with `--join-token <token>` (an Operator mints it: `create-join-token [--expires 24h] [--uses 50]`). It exchanges the token for its own credential at `POST /api/agents/enroll`, stores it at `<data>\credential` — DPAPI machine scope, with an ACL of SYSTEM, Administrators and the service account — and never needs the option again. Do not leave `--join-token` in a service's `binPath`: `sc qc` shows it to anyone who can query the service, and an agent that already holds a credential ignores it (with a warning in its log). A name holds one live credential at a time: to move an agent to a new machine, revoke first (`revoke-agent --name X`, or `DELETE /api/agents/X`, which revokes as it deregisters), then enroll the new one. An agent whose credential is rejected keeps everything it runs and says so in its log once per five minutes ([Runbook §3.19](Runbook.md)); re-enrolling is a restart with a new join token.
- **The portal signs people in with Windows** (Negotiate on Kestrel: Kerberos in a domain, NTLM against local accounts on a workgroup machine). Two group names decide the roles — `Authentication:Windows:OperatorsGroup` (every page, every change) and `ViewersGroup` (every page, no changes); a person in neither gets a page naming the groups, and the portal keeps no user list of its own. Under a dedicated domain service account, register `HTTP/<portal fqdn>` as an SPN for that account (`setspn -S`), or Kerberos silently falls back to NTLM and fails across machines; under *Network Service* or *Local System* the machine account already has one. Under IIS, enable Windows Authentication on the site instead; the in-process handler steps aside. The portal's URL must be in the browsers' intranet zone for credentials to be sent without a prompt.
- **The portal's own key** is an Operator API key, and root-equivalent: on the control plane host, `create-api-key --name portal --role Operator --expires never`; on the portal host, `Enlist.Portal.exe protect <key>` prints a `dpapi:` value for `ControlPlane:ApiKey` that only that machine can decrypt. DPAPI keeps the key out of a casual read and a backup; the file's ACL (the service account and administrators) is the control. A portal with no key logs a warning at startup and works only against a control plane running `Off`.
- **Tools present an API key**: `enlist-deploy --api-key <key>`, or `ENLIST_API_KEY` in the environment (a pipeline; a command line is visible in process listings). Mint them from the Access page or the CLI with the role the tool needs — a pipeline that uploads is an Operator, a dashboard or a reverse proxy reading the endpoint feed is a Viewer — and revoke them by name.
- **The portal has the same two listener rules** as the control plane: `Off` only on loopback, and `https://` off loopback under `Required`. The demo runs both `Off` on loopback by choice; a real deployment runs both `Required` over TLS.

## 2. Environments

No environment-specific configuration files exist beyond `appsettings.Development.json` for the control plane and portal. A real rollout needs at minimum:

| Environment | Suggested distinguishing config |
|---|---|
| Development | LocalDB, `http://localhost` URLs — current default. |
| Staging/Test | A dedicated SQL Server database and control-plane host, separate `PackageBlobs` storage, separate agent fleet (or a labeled subset of the production fleet, e.g. `env=staging`). |
| Production | Production SQL Server (with backup/HA per the DBA's own standards, not specified by this codebase), TLS-terminated endpoints, migrations run as an explicit pipeline step per §1.4. |

## 3. Rollback Strategy

- **Application rollback** is a policy edit, **not** a re-run of `enlist-deploy`. That CLI takes `--source` and only uploads; it creates no placement, so re-running it cannot roll anything back. Point the rule at an older digest instead — `PUT /api/application-policies/{id}` with a previous `PackageDigest`, or the same edit in the portal. Because packages are content-addressed and retained for the configured grace period (default 30 days) after becoming unreferenced — and never collected while a rule still references them — an older build is very likely still present without needing to be re-uploaded.
- **Database rollback** is not addressed by this codebase — EF Core migrations are forward-only as authored; a down-migration would need to be written and tested like any other, using standard EF Core tooling (`dotnet ef migrations remove` / a hand-authored down migration), which is outside this system's own scope.
- **Control plane / Portal rollback** — redeploy the previous published build; both are stateless application tiers (all durable state is in SQL Server and the package blob store), so a version rollback of either is not expected to require any data migration by itself, only a compatible database schema.

---

## 4. Proposed IaC (not yet implemented)

The following is a **starting point**, not a description of anything currently in this repository. It follows directly from §1's real deployment shape.

### 4.1 Example `Dockerfile` — Enlist.ControlPlane

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Enlist.ControlPlane/Enlist.ControlPlane.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Enlist.ControlPlane.dll"]
```

The same two-stage shape applies to `Enlist.Portal`. **`Enlist.Agent` is a poor fit for a container** — it manages Windows Service Control Manager lifecycle and spawns/supervises OS processes (including, optionally, a Windows Job Object) directly on the host it's protecting; it is meant to run on the bare managed machine, not inside a container on that machine.

### 4.2 Example `docker-compose.yml` (development/staging convenience)

```yaml
services:
  sqlserver:
    image: mcr.microsoft.com/mssql/server:2022-latest
    environment:
      ACCEPT_EULA: "Y"
      MSSQL_SA_PASSWORD: "<use a secret, not a literal here>"
    ports: ["1433:1433"]

  controlplane:
    build:
      context: .
      dockerfile: src/Enlist.ControlPlane/Dockerfile
    environment:
      ConnectionStrings__ControlPlane: "Server=sqlserver;Database=EnlistControlPlane;User Id=sa;Password=<same secret>;TrustServerCertificate=True;"
    ports: ["5293:8080"]
    depends_on: [sqlserver]

  portal:
    build:
      context: .
      dockerfile: src/Enlist.Portal/Dockerfile
    environment:
      ControlPlane__BaseUrl: "http://controlplane:8080"
    ports: ["5231:8080"]
    depends_on: [controlplane]
```

### 4.3 Example CI Pipeline Stages

1. `dotnet restore enList_v3.slnx`
2. `dotnet build enList_v3.slnx --configuration Release --no-restore`
3. `dotnet test enList_v3.slnx --configuration Release --no-build` — gate on all 121 tests passing (see [`Test-Plan.md`](Test-Plan.md)).
4. `dotnet publish` each of the five components (§1.5).
5. Run pending EF Core migrations against the target database as an explicit step (replacing the app's own auto-migrate — §1.4).
6. Deploy `Enlist.ControlPlane` and `Enlist.Portal` build outputs to their hosts (container image push + orchestrator rollout, or a direct file copy + service restart, depending on chosen hosting).
7. Distribute the updated `Enlist.Agent` + `Enlist.Runner` build outputs to managed machines and restart the `enlist-agent` Windows Service on each (out of band from the control-plane/portal deploy — agents do not need to be redeployed in lockstep with the control plane unless the wire contract itself changed).

### 4.4 Secrets

No secret currently needs to cross a network boundary as part of enList's own protocol (agents authenticate to nothing — there is no auth layer at all, see [`BRD.md` §5.2](../04-requirements/BRD.md#52-out-of-scope-not-present-in-the-current-implementation)). The only secret an IaC pipeline needs to manage today is the SQL Server connection string / credentials for the control plane, which should come from the deployment platform's own secret store (e.g. a Kubernetes `Secret`, an Azure App Service connection-string app setting marked as a secret, or equivalent), never committed to `appsettings.json`.
