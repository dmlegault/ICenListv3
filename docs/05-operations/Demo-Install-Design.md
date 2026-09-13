# enList Demo Install — Design

**Product:** enList v3
**Document status:** Design draft, 2026-09-11. Companion to [`Installer-UI-Design.md`](Installer-UI-Design.md), which designs the production installer; this document designs the **Demo** option. **Nothing here is implemented as an installer** — but, unusually for a design draft, everything it describes has a working prototype: the checked-in demo tooling in [`demo/`](../../demo/README.md) (`start-demo.ps1`, `demo-status.ps1`, `sql-server/demo-db.ps1`) and the runtime layout in [`.demo/`](../../.demo/README.md). This is the design for productizing that into something a person with no repository and no build tools can run.

---

## 1. What a demo machine is for

A presenter, or a prospect evaluating on their own, brings up a **complete, live enList fleet on one machine in minutes**, with something already running to look at, and can put it back to a known state between demos. It is optimized for time-to-first-portal and repeatability. It is not a deployment, and it should not pretend to be one.

"Complete and live" means what today's demo means, because that is what has been shown to work:

- a control plane and a portal;
- **two agents**, with different tags — a fleet in miniature, so targeting-by-tag is real, not a diagram;
- the **four sample applications** deployed and running under **five policy rules**, one of them a `net472` application on the legacy runner;
- **one application running in a container** on one of the agents, when the machine can run containers;
- application output flowing into the Logs tab;
- the portal open on the Applications tab when the launcher finishes.

---

## 2. Recommendation: a *Demo* install type in the same bundle, run as a session launcher, not as services

**Add a fourth install type, *Demo (everything on this machine)*, to the bundle designed in `Installer-UI-Design.md`, and run the demo fleet as processes in the presenter's session, started and stopped by a launcher — not as Windows services.**

The observation underneath this: **a demo does not need services.** Today's demo already runs the whole fleet as user-session processes (`start-demo.ps1`), and that is exactly the right shape for a demo laptop:

- **Double-click to start, close to stop, nothing left running.** No service accounts, no *Log on as a service*, nothing to clean up when the laptop goes back to being a laptop.
- **No database administration.** The control plane's built-in default connection string is SQL Server Express **LocalDB** ([`appsettings.Development.json`](../../src/Enlist.ControlPlane/appsettings.Development.json) - Development only since 2026-09-13; outside Development the process refuses to start without an explicit connection string rather than falling back), and in the *Development* environment it creates the database and schema itself on first start (§1.4 of [`Deployment-IaC.md`](Deployment-IaC.md), the auto-migrate branch). LocalDB is per-user and does not work well under a service account — which is a second reason the services model is wrong for a demo and the session model is right.
- **Nothing exposed.** Everything binds to `localhost`. Authentication is **off**, which is permitted **only** because everything is on loopback - the hard rule of [Authentication-Design.md](../03-architecture/Authentication-Design.md) section 8, enforced at startup. A demo laptop on conference Wi-Fi must not put the control plane on the network, and a session launcher makes that the default rather than a configuration step. Under the C2 design this is precisely the one case authentication may be off - loopback only, a hard rule ([Authentication-Design.md](../03-architecture/Authentication-Design.md) section 8).
- **Reset is a first-class action.** Drop the database, clear the blobs and the agents' data, re-seed — a known state every time, which a service-based install makes awkward.

**Why the same bundle and not a separate installer:** the demo installs the same three published payloads (control plane, portal, agent with both runners) as a Server install, plus one extra package. Reusing the MSIs and the prerequisite chain means one artifact to build, sign and publish. A demo-only bundle is a flag away if distribution ever needs it (§9).

**Where services *are* the answer:** a shared demo server that should survive reboots and be reachable by a team is not a demo laptop; it is the *Server* install type with the same seed applied afterwards. §7 covers that, and it is deliberately the same seed.

---

## 3. What the demo contains

| Component | How it runs | Detail |
|---|---|---|
| **Database** | SQL Server Express **LocalDB**, chained as a bundle prerequisite (~55 MB, silent) | The control plane's default; the database is created on first start. **Optional:** when `wslc` is present, the launcher can instead run the SQL Server container `demo/sql-server/demo-db.ps1` manages today — SSMS-visible, "looks like production" — with the image pulled on demand. It is not bundled: at about 1.5 GB it would triple the installer for something most demos never open |
| **Control plane** | Session process, `http://localhost:5293`, *Development* environment | Development so it creates the schema itself. `PackageStorage:Root` under the demo data directory (§8) |
| **Portal** | Session process, `http://localhost:5231`, *Development* environment | Opened automatically when the fleet is up. Development so static web assets serve from the published output the same way they do in the repo demo |
| **DEV-AGENT-01** | Session process, tags `env=demo`, `dev=test9` | Process isolation for everything it runs |
| **DEV-AGENT-02** | Session process, tags `env=demo`, `dev=test10`, `--container-engine wslc` when detected | Runs OrderProcessor in a container — the one visible difference between the two agents, and the reason there are two |
| **Applications** | The four samples, pre-built and bundled as package zips | `SampleService`, `OrderProcessor`, `DataPipeline` (net10.0), `LegacySample` (net472) |
| **Policy rules** | Five, created by the seed | The topology in [`demo/README.md` §Populating the demo](../../demo/README.md): `SampleService` and `LegacySample` on `env=demo` (both agents); `DataPipeline` and `OrderProcessor` on `dev=test9` (DEV-AGENT-01); `OrderProcessor` again on `dev=test10` as a **container** (DEV-AGENT-02) |
| **Runner image** | `enlist/runner:<version>`, bundled as a tarball, `wslc load`-ed at install when `wslc` is present | 203 MB. Without `wslc` the tarball is skipped and the container rule becomes a demo point of its own (§10, step 3) |
| **Demo guide** | HTML, opened from the Finished page and the Start Menu | §10, as a page |

Two agents on one machine is not a compromise; it is the demo. Tag targeting, the container chip, and "this application runs on two agents" are all things a single agent cannot show.

---

## 4. Prerequisites

| Prerequisite | Handling |
|---|---|
| .NET 10 runtime and ASP.NET Core runtime | Bundle installs them, as for every install type |
| **SQL Server Express LocalDB** | Bundle installs it (`SqlLocalDB.msi`, ~55 MB); skipped when already present |
| .NET Framework 4.7.2 | Inbox on Windows 10/11 and Server 2019+. Needed only by `LegacySample`; informational if absent |
| **WSL 2.9.11+ with `wslc`** | Optional. Detected exactly as the main installer does (§4 there). Enables the runner image load and DEV-AGENT-02's container engine. Not installable by the bundle; the page shows `wsl --update` and continues |
| Disk, memory | ~1 GB (binaries, runner image, seed); 4 GB RAM is comfortable |

A demo must **never** fail to install for lack of a container engine. It installs, runs everything in-process, and the missing engine becomes something to show (§10).

---

## 5. The install flow

The Demo type pre-answers every configuration page of the main installer. The flow is:

```
Welcome → License → Install type [Demo] → Prerequisites (automatic) → Demo location → Installing → Finished
```

The one page a person sees is **Demo location**:

```
 ┌─ Demo ──────────────────────────────────────────────────────────────┐
 │ Install to     [C:\Program Files\enList\Demo                ] [...] │
 │ Demo data      [C:\Users\you\AppData\Local\enList\Demo      ] [...] │
 │                database, package blobs, both agents' state, logs    │
 │ [x] Include the container demo (wslc 2.9.11 detected)               │
 │ [x] Start the demo when this finishes                               │
 └─────────────────────────────────────────────────────────────────────┘
```

| Field | Default | Notes | Property |
|---|---|---|---|
| Install to | `%ProgramFiles%\enList\Demo` | Published binaries for all three components plus the launcher and seed | `DEMO_INSTALLDIR` |
| Demo data | `%LocalAppData%\enList\Demo` | Per-user on purpose: the launcher runs as the user, so no ACL work, and *Reset* can delete it freely | `DEMO_DATADIR` |
| Include the container demo | checked when `wslc` was detected, disabled otherwise | Loads the runner image and gives DEV-AGENT-02 `--container-engine wslc` | `DEMO_CONTAINERS` |
| Start when finished | checked | Runs the launcher from the Finished page | — |

**Finished** offers *Start the demo* and *Open the demo guide*. Silent install is one property: `INSTALLTYPE=Demo`.

---

## 6. The launcher — what "Start the demo" does

This is `start-demo.ps1` plus the *Populating the demo* steps, productized. Each step waits for the previous one to be genuinely ready, the way the script does today, because a portal opened two seconds early is a blank page.

1. **Database.** Start the LocalDB instance (`sqllocaldb start mssqllocaldb`), or bring up the SQL Server container if that mode was chosen.
2. **Control plane**, Development environment, on `:5293`. Wait for `GET /health` → **200** — the endpoint added on 2026-09-11 for exactly this kind of caller.
3. **Both agents**, with `--container-engine wslc` and the bundled image for DEV-AGENT-02 when containers were included. Wait until both have reported.
4. **Portal** on `:5231`. Wait for 200.
5. **Seed, first run only.** Idempotent: if `GET /api/packages` already lists the four applications, skip. Otherwise: `PUT /api/agents/{name}/tags` for both agents; upload the four bundled package zips (`POST /api/packages?application=…`); `POST /api/application-policies` for the five rules. Then wait until `report/latest` for both agents shows their applications Running.
6. **Open** `http://localhost:5231/`.

A **status window** — the `demo-status.ps1` view: each process with its port, the database, the container, and each agent's applications — stays open while the demo runs. **Stop** (or closing the window) stops the fleet in order: agents first, so applications shut down through the runner's shutdown path and containers are stopped cleanly; then the portal and control plane.

**Reset** stops the fleet, drops the LocalDB database (or resets the container), deletes the package blobs and both agents' data directories, and marks the seed as pending, so the next *Start* rebuilds the demo from zero. This is `demo-db.ps1 reset -Force` followed by the populate steps, made into one button — the thing a presenter needs between two back-to-back demos where the first one deleted a rule.

**v1 and v2.** v1 is the three PowerShell scripts as they stand, installed with Start Menu shortcuts — *Start enList Demo*, *Demo status*, *Reset demo* — and a console window as the status view. They exist and have been exercised repeatedly; shipping them is a packaging task. v2 is a small tray application with *Start / Stop / Reset / Open portal / Status* behind buttons and the same steps behind them. Nothing about the fleet changes between the two.

---

## 7. The same seed on a Server install

A persistent demo environment — a VM a team shares, or a prospect's pilot — wants the *Server* install type (real services, a real SQL Server, reboot-safe) **and the same seed**. So the seed is a component, not a demo-only feature:

- The *Ready to install* page of a Server install gains one checkbox, **Seed with the sample applications** (`DEMO_SEED=1`), off by default.
- After the services are up, the BA runs the same step 5 as §6 against the new control plane.
- The differences are the ones you would expect: the agents are whichever real agents register (the local one, plus any installed elsewhere with the *Agent only* type), so the tags are the ones you set on the Agents tab; and the sample applications become the first things a pilot replaces with its own.

One seed, two homes. That is also why the seed's policy topology is expressed as data (`topology.json` in the seed folder) rather than in launcher code.

---

## 8. Layout and binding

| | Path |
|---|---|
| Binaries | `%ProgramFiles%\enList\Demo\{ControlPlane,Portal,Agent,Launcher}` |
| Seed | `%ProgramFiles%\enList\Demo\Seed\` — four package zips, `topology.json`, `enlist-runner.tar` |
| Data (per user) | `%LocalAppData%\enList\Demo\{ControlPlane\PackageBlobs, Agent-01, Agent-02, Logs}` |
| Guide | `%ProgramFiles%\enList\Demo\Guide\index.html`, on the Start Menu |

**Ports:** `5293` and `5231`, bound to **`localhost` only**. The demo runs with authentication off, and the control plane will not start that way on any other address - so the binding is not merely a policy here, it is what makes the demo legal.  A single checkbox in the status window, *Allow other devices on this network to open the portal*, rebinds the portal to all interfaces for the length of that session — with the warning spelled out — for the case of showing the portal from a tablet rather than the projector.

---

## 9. A separate demo installer?

The MSIs are shared, so a demo-only bundle (`enList-Demo-Setup.exe`) is inexpensive: the same bootstrapper application with a different default flow. It is worth producing when either of these is true:

- the demo is to be **distributed to people who should not receive the production installer** — a download link for prospects;
- the download must be **smaller** than the full bundle. (It cannot be much smaller — the demo needs all three components — but it can drop the legacy runner and the `net472` sample if the audience does not care.)

Until one of those is true, the install type is enough. Recommendation: build the install type; add the separate bundle when someone asks for the link.

---

## 10. The demo walkthrough

The storyline the guide page tells, in the order the portal supports it. Everything here exists today; the two steps marked *terminal* are shown from a console because that is the honest way to show them.

1. **Applications tab.** Four applications with their version badges and rule counts. Expand one: which agents run it, and each service and job with its state. *"A fleet of two, running four applications, none of which were written for enList — one is a .NET Framework 4.7.2 app on its own runner."*
2. **Agents tab.** Two agents, their tags, *Online*, and the containers chip on DEV-AGENT-02 — hover it: *WSL container engine (wslc) reachable*. *"Targeting is by tag. These two differ by one tag, and that is why OrderProcessor runs differently on each."*
3. **Rules.** Open OrderProcessor's policy screen: two rules, one on `dev=test9` as a process, one on `dev=test10` as a container. *(Without `wslc`: the container rule fails on DEV-AGENT-02 with the reason spelled out on the Agents tab — the capability chip says it cannot run containers. That is the demo point instead: the system tells you why.)*
4. **Live control.** From an application's expanded panel, **Stop** a service; watch its state; **Start** it again. Same for a job. *"Imperative control on top of declared policy, and the portal shows the state the agent reports, not the state it hopes for."*
5. **Logs tab.** Application output arriving — the heartbeat services write a line every few seconds. Filter to one agent and one application.
6. **Resilience** *(terminal)*. Kill the OrderProcessor container (`wslc kill …`) or a runner process. Watch the application go to *Failed* and come back *Running* — the agent's crash restart — within seconds. *"Nobody clicked anything."*
7. **Rollout.** From the application's policy screen, **Manage Packages → Upload** a new version (a second copy of a sample zip with a bumped version is in the seed folder for this), then edit the rule to select it. The agents download it and restart the application onto it.
8. **Exclusion.** On the Agents tab, toggle DEV-AGENT-02's scheduling off: everything it runs stops. Toggle it back: everything returns. *"Maintenance mode is one switch."*
9. **Reset** *(terminal, or the launcher's button)*. Back to the seeded state for the next audience.

Ten minutes. Almost every claim in it is backed by a test in [`Test-Plan.md`](Test-Plan.md) - the exception is step 4, imperative Start/Stop, which Test-Plan lists twice as not automated. Either close that gap or drop the claim; it should not be both.

---

## 11. What this is, relative to the `demo/` tooling

| Today (`demo/`, in the repository) | The Demo install |
|---|---|
| `start-demo.ps1` builds nothing but runs from `bin/Debug` of a built repository | Published binaries, no repository, no SDK |
| Packages are built from `samples/` and uploaded with `enlist-deploy` | Pre-built zips in the seed folder, uploaded by the launcher |
| SQL Server in a `wslc` container (`demo/sql-server`) | LocalDB by default; the container as an option |
| Control plane as *Production* (verifies the schema; migrations applied by hand) | *Development* (creates the schema itself) — a demo has no DBA |
| `.demo/` under the repository | `%LocalAppData%\enList\Demo` |
| `demo-status.ps1` in a console | The status window |
| `demo-db.ps1 reset -Force` + the populate steps | *Reset* |

The scripts are the specification of the launcher's behaviour and stay the reference; when the launcher and a script disagree, one of them has a bug.

---

## 12. Open decisions

1. **Install type, or a separate bundle?** **Install type first**; the separate bundle when a distribution need appears (§9).
2. **LocalDB or the SQL Server container by default?** **LocalDB.** It is the control plane's own default, needs no image, and a demo rarely opens SSMS. The container stays available for the audience that does.
3. **Launcher v1 as the existing scripts, or build the tray app first?** **Scripts first** — they are proven; the tray app is polish.
4. **Bundle the runner image (203 MB)?** **Yes.** A demo that pulls from a registry is a demo that fails on hotel Wi-Fi.
5. **Bind to `localhost` only?** **Yes**, with the per-session opt-in in §8. Not negotiable, and no longer merely a preference: authentication-off is only honoured on loopback, so the opt-in has to turn authentication ON as well as rebinding - which is the part §8 still has to design.
6. **Seed content.** The current four samples, as they are. A purpose-built "storyline" application (one that visibly does something an audience recognises) is worth writing later; it is content, not installer work.
7. **Product version.** **Done (2026-09-11):** `3.0.0`, set once in `Directory.Build.props` at the repository root, so every assembly, `/health` and the demo guide agree.

---

*Related:* [`Installer-UI-Design.md`](Installer-UI-Design.md) (the production installer this is a mode of) · [`demo/README.md`](../../demo/README.md) (the working prototype and its runbook) · [`.demo/README.md`](../../.demo/README.md) (the runtime layout the data directory mirrors) · [`Deployment-IaC.md`](Deployment-IaC.md) §1.4 (why Development mode is what makes a zero-DBA demo possible).
