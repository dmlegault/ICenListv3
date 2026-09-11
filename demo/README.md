# Running the enList demo

This directory is the checked-in tooling for the local demo fleet. Two things live here:

- **[`start-demo.ps1`](start-demo.ps1)** — starts (or restarts) the whole fleet with one command.
- **[`sql-server/`](sql-server/)** — the demo's SQL Server, run in a WSL container by
  [`sql-server/demo-db.ps1`](sql-server/demo-db.ps1). See its [own README](sql-server/README.md).

The fleet it brings up:

| Piece | Where | Started by |
|---|---|---|
| SQL Server (`EnlistControlPlane` database) | `127.0.0.1,14330` | `demo/sql-server/demo-db.ps1` (via `start-demo.ps1`) |
| Control plane | `http://localhost:5293` | `start-demo.ps1`, with authentication off — which the control plane permits only on loopback |
| DEV-AGENT-01 (all applications in-process) | — | `start-demo.ps1` |
| DEV-AGENT-02 (one application in a `wslc` container) | — | `start-demo.ps1` |
| Portal | `http://localhost:5231` | `start-demo.ps1` |

Everything the agents write to disk lands in [`.demo/`](../.demo/) (generated runtime state, not
this directory). The demo's actual data lives in the SQL Server, which persists between runs.

## Run it

Once the one-time setup below is done, this is the whole thing:

```powershell
./demo/start-demo.ps1
```

It is idempotent and safe to re-run: it stops any control plane already on `:5293` and any running
demo agent first (a second agent with the same name would silently corrupt that agent's reports),
brings the SQL Server up, starts the control plane pointed at it, starts both agents, waits for both
to report, and prints where everything is. About 30–40 seconds from cold.

```powershell
./demo/start-demo.ps1 -Stop           # stop the control plane and agents (SQL Server stays up)
./demo/start-demo.ps1 -ContainerEngine docker   # put DEV-AGENT-02's container app on Docker instead of wslc
./demo/start-demo.ps1 -JoinToken enlj_...        # enroll both agents, once - see Enrolling the demo agents
```

Stopping the SQL Server too, when you want the machine quiet:

```powershell
./demo/sql-server/demo-db.ps1 down
```

To see everything the demo is running at a glance — the control plane, both agents and the portal
with their PIDs and ports, the SQL Server and application containers, and each agent's live
application states — without starting or stopping anything:

```powershell
./demo/demo-status.ps1
```

## One-time setup

1. **Prerequisites.** .NET 10 SDK; WSL 2.9.11+ for `wslc` (`wsl --update`) — the same engine
   DEV-AGENT-02 uses; SSMS or Azure Data Studio if you want to watch the database. No Docker
   required (pass `-ContainerEngine docker` only if you specifically want it).

2. **Build the solution** — the control plane, both agents and both runners:

   ```powershell
   dotnet build enList_v3.slnx
   ```

3. **Build the container runner image** DEV-AGENT-02 needs:

   ```powershell
   wslc build -f src/Enlist.Runner/Dockerfile -t enlist/runner:wslc .
   ```

4. **Create the database password.** Copy `demo/sql-server/.env.example` to `demo/sql-server/.env`
   and set `MSSQL_SA_PASSWORD` (SQL Server's complexity rules apply; no double quotes). See the
   [sql-server README](sql-server/README.md).

5. **Apply the schema.** The control plane runs as **Production** here, so it *verifies* the schema
   and refuses to start on a pending migration rather than creating it. Bring the server up and
   apply migrations once (and again whenever a new migration is added):

   ```powershell
   ./demo/sql-server/demo-db.ps1 up
   $env:ConnectionStrings__ControlPlane = ./demo/sql-server/demo-db.ps1 connstring
   dotnet ef database update --project src/Enlist.ControlPlane
   ```

6. **Populate it** — upload the four sample packages and declare where each runs. See
   [Populating the demo](#populating-the-demo) below. Skip this if the database already has the demo
   data (it survives reboots and resets only when you tell it to).

After that, `./demo/start-demo.ps1` is all you need day to day.

## Populating the demo

Do this on a fresh database (first run, or after `reset -Force`). The fleet must be running, since
the agents self-register when `start-demo.ps1` starts them; run `./demo/start-demo.ps1` first.

The demo's shape is two tagged agents and one policy rule per application, targeted by tag:

| Agent | Tags |
|---|---|
| DEV-AGENT-01 | `env=demo`, `dev=test9` |
| DEV-AGENT-02 | `env=demo`, `dev=test10` |

| Application | Tag selector | Isolation | Runs on |
|---|---|---|---|
| SampleService | `env=demo` | process | both agents |
| LegacySample (net472) | `env=demo` | process | both agents |
| DataPipeline | `dev=test9` | process | DEV-AGENT-01 |
| OrderProcessor | `dev=test9` | process | DEV-AGENT-01 |
| OrderProcessor | `dev=test10` | **container** | DEV-AGENT-02 |

**1. Tag the two agents.** Targeting is entirely by tag. In PowerShell (`curl.exe` is the real curl,
not PowerShell's alias):

```powershell
curl.exe -X PUT http://localhost:5293/api/agents/DEV-AGENT-01/tags -H "Content-Type: application/json" -d '{\"tags\":{\"env\":\"demo\",\"dev\":\"test9\"}}'
curl.exe -X PUT http://localhost:5293/api/agents/DEV-AGENT-02/tags -H "Content-Type: application/json" -d '{\"tags\":{\"env\":\"demo\",\"dev\":\"test10\"}}'
```

**2. Build and upload the four sample packages.** Each sample builds into `deploy/<App>` (its
`OutputPath`); `enlist-deploy` zips that folder, uploads it, and prints a `digest:` a policy will
point at:

```powershell
foreach ($p in 'Service','OrderProcessor','DataPipeline','Legacy') {
    dotnet build "samples/Enlist.Sample.$p/Enlist.Sample.$p.csproj"
}
$deploy = 'src/Enlist.Deploy/bin/Debug/net10.0/enlist-deploy.dll'
foreach ($app in 'SampleService','OrderProcessor','DataPipeline','LegacySample') {
    dotnet $deploy --control-plane http://localhost:5293 --app $app --source "deploy/$app"
}
```

**3. Declare one policy rule per row of the table above.** Easiest in the **portal's Applications
tab** (`enlist-deploy` even points you there): expand an application, add a rule, pick the uploaded
version and the tags. The API equivalent, substituting each app's digest from step 2 — note the
extra `isolation` for the container rule:

```powershell
$cp = 'http://localhost:5293'
# process rules (env=demo -> both agents; dev=test9 -> DEV-AGENT-01)
curl.exe -X POST $cp/api/application-policies -H "Content-Type: application/json" -d '{\"applicationName\":\"SampleService\",\"desiredState\":\"Running\",\"tagSelector\":{\"env\":\"demo\"},\"packageDigest\":\"<SampleService-digest>\"}'
curl.exe -X POST $cp/api/application-policies -H "Content-Type: application/json" -d '{\"applicationName\":\"LegacySample\",\"desiredState\":\"Running\",\"tagSelector\":{\"env\":\"demo\"},\"packageDigest\":\"<LegacySample-digest>\"}'
curl.exe -X POST $cp/api/application-policies -H "Content-Type: application/json" -d '{\"applicationName\":\"DataPipeline\",\"desiredState\":\"Running\",\"tagSelector\":{\"dev\":\"test9\"},\"packageDigest\":\"<DataPipeline-digest>\"}'
curl.exe -X POST $cp/api/application-policies -H "Content-Type: application/json" -d '{\"applicationName\":\"OrderProcessor\",\"desiredState\":\"Running\",\"tagSelector\":{\"dev\":\"test9\"},\"packageDigest\":\"<OrderProcessor-digest>\"}'
# container rule (dev=test10 -> DEV-AGENT-02, runs OrderProcessor in a wslc container)
curl.exe -X POST $cp/api/application-policies -H "Content-Type: application/json" -d '{\"applicationName\":\"OrderProcessor\",\"desiredState\":\"Running\",\"tagSelector\":{\"dev\":\"test10\"},\"packageDigest\":\"<OrderProcessor-digest>\",\"isolation\":{\"mode\":\"container\"}}'
```

Within a few seconds each agent downloads its packages and starts them; confirm in the portal or with
`GET /api/agents/DEV-AGENT-01/report/latest`.

## Enrolling the demo agents (optional)

The demo control plane runs with authentication **off** — permitted only because it listens on loopback — so
the agents need no credential and start without one. To show enrollment, the way a real fleet is brought
up ([Authentication-Design.md §4](../docs/03-architecture/Authentication-Design.md)), mint a join token
against the demo database and start the demo with it once:

```powershell
$env:ConnectionStrings__ControlPlane = ./demo/sql-server/demo-db.ps1 connstring
dotnet src/Enlist.ControlPlane/bin/Debug/net10.0/Enlist.ControlPlane.dll create-join-token
./demo/start-demo.ps1 -JoinToken enlj_...
```

Each agent's log (`.demo/logs/agent-0N.log`) then says `Enrolled as 'DEV-AGENT-0N'`, and the credential
sits at `.demo/agent-0N/credential` — DPAPI-protected, readable by SYSTEM, Administrators and you. From
then on a plain `./demo/start-demo.ps1` starts the agents with their stored credentials (`with its
credential` in the startup line); the join token is not needed again, and an agent that already holds a
credential ignores `-JoinToken` and says so. `list-join-tokens` and `revoke-agent --name DEV-AGENT-01` are
the other verbs worth showing: a revoked agent keeps running what it runs and logs why, once per five
minutes ([Runbook §3.19](../docs/05-operations/Runbook.md)). To start over, delete
`.demo/agent-0N/credential` and revoke the name.

## Redeploying a code change

A policy pins a specific package **digest**, so changing a sample's code and rebuilding does nothing
on its own — the agents keep running the version the policy names. To ship the change: rebuild,
upload it as a new version, and re-point the policy at the new digest. The agent then downloads it and
restarts the application onto it.

```powershell
dotnet build samples/Enlist.Sample.Service/Enlist.Sample.Service.csproj
dotnet src/Enlist.Deploy/bin/Debug/net10.0/enlist-deploy.dll --control-plane http://localhost:5293 --app SampleService --source deploy/SampleService   # prints the new digest

# find the rule's id, then re-point it (a PUT with just the digest is a partial update)
curl.exe http://localhost:5293/api/application-policies
curl.exe -X PUT http://localhost:5293/api/application-policies/<policy-id> -H "Content-Type: application/json" -d '{\"packageDigest\":\"<new-digest>\"}'
```

In the portal this is the same two clicks: upload from the app's **Manage Packages** screen, then edit
the rule to select the new version.

## The portal

`start-demo.ps1` starts the portal on `http://localhost:5231` alongside everything else — it is one
of the demo processes, stopped and restarted with the rest, not a separately-managed service. It
reads the control plane over HTTP at `:5293` and never touches SQL, so nothing about it changes when
the database moves.

> A production portal *is* a long-lived service, installed and hosted separately from any demo — that
> deployment shape is documented in [`docs/05-operations/Deployment-IaC.md`](../docs/05-operations/Deployment-IaC.md).
> The demo deliberately does not use it, so the whole fleet has one lifecycle and one command.

It runs with authentication **off** as well — the same `Authentication__Mode=Off` the control plane gets, permitted
because it listens on loopback — so there is no Windows sign-in and everyone is an Operator. The *Access* page
still works: mint a join token or an API key there and use it against the demo control plane, which accepts a
key even while `Off`. A real portal runs `Required` — Windows sign-in, two group names, its own key
([Deployment-IaC §1.8](../docs/05-operations/Deployment-IaC.md)).

## Start from zero

To wipe the demo and rebuild it clean — destroys the database **and** the package blobs:

```powershell
./demo/sql-server/demo-db.ps1 reset -Force
```

Then bring the fleet up (`./demo/start-demo.ps1`), apply the schema (one-time setup step 5), and run
[Populating the demo](#populating-the-demo) to re-tag the agents, re-upload the packages, and
recreate the policy rules.

## Troubleshooting

- **"The control plane did not answer on :5293 within 60s."** Almost always a pending migration —
  the demo runs as Production and will not self-migrate. Run setup step 5, then start again. The tail
  of `.demo/logs/control-plane.log` says which migration.
- **An agent shows applications as `Failed`.** Check `.demo/logs/agent-0X.err.log` and the agent's
  own log under `.demo/agent-0X/Logs/`. A container application failing on DEV-AGENT-02 usually means
  the `enlist/runner:wslc` image is missing — rebuild it (setup step 3).
- **"stale" or missing data in the portal.** Confirm exactly one process per agent name:
  `Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'dotnet.exe' -and $_.CommandLine -like '*enlist-agent.dll*' }`
  should return two rows. `start-demo.ps1` enforces this, so this only bites if agents were started
  by hand as well.
- **wslc problems** (SQL Server or the agent's containers): see the troubleshooting section of the
  [sql-server README](sql-server/README.md).

## See also

- [`.demo/README.md`](../.demo/README.md) — the generated runtime state this fleet writes.
- [`sql-server/README.md`](sql-server/README.md) — the demo database in detail.
- [`docs/01-start-here/Developer-Setup-Guide.md`](../docs/01-start-here/Developer-Setup-Guide.md) —
  the full walkthrough, including deploying samples and declaring policies.
