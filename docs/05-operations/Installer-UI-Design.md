# enList Installer — UI Design (WiX)

**Product:** enList v3
**Document status:** Design draft, 2026-09-11; **revised 2026-09-13** so the listen defaults, the duplicate-name check and the authentication section describe what the platform actually does (see §6.4, §6.7 and §8). **Nothing in this document is implemented** — there is no installer project in the repository yet. It exists to agree on the shape of the installer, page by page, before any WiX is authored. Where it describes current product behaviour (ports, flags, config keys) it is accurate to the code; where it describes the installer it is a proposal.

> **Read with [`Deployment-IaC.md`](Deployment-IaC.md).** That document records how the platform is deployed *today* (by hand, with `sc.exe`). This one proposes the installer that replaces those hand steps, and its silent-install surface (§10) is what the IaC shape proposed in Deployment-IaC §4 would drive.

---

## 1. What the installer has to do

Two requirements, restated with what they imply:

1. **Install the portal and/or the control plane, each either as a Windows service or in a container.** Implies: they are separately selectable; hosting mode is a per-component choice; the installer knows how to create a service *and* how to provision a container; and it collects an install path, a listen address, a service logon account, and (for the control plane) a database connection.
2. **Install the agent on its own** — an agent-only install on a machine that will never host the control plane. Implies: the agent is its own installable unit; the installer detects whether the machine can run containers (`wslc` and/or Docker) and configures the agent accordingly; it asks for the control plane URL and **verifies it before finishing**; it asks for an install path; and — "provide a login for the portal / control plane if they are installed as a service" — it collects credentials where the product actually has a place to put them (§8 is candid about where it does not).

Everything else below follows from those two.

---

## 2. Recommended shape: one bundle, three MSIs, a managed bootstrapper application

**Use a Burn bundle with a managed (C#/WPF) bootstrapper application (BA), chaining three small MSIs — `Enlist.ControlPlane.msi`, `Enlist.Portal.msi`, `Enlist.Agent.msi` — plus the prerequisite packages.** WiX 5.

Why that and not a single MSI with custom dialogs:

- **The required pages are beyond MSI's dialog toolkit.** "Verify this URL" with a spinner and a result, "test this database connection", "detect wslc and Docker and grey out what is missing" — MSI's built-in UI can do none of these without deferred custom actions fighting the sequencing engine. A managed BA is ordinary WPF: a `Verify` button awaits an `HttpClient` call and updates a label. All detection and verification (§4, §6) lives in the BA, on the UI thread, **before** anything is installed.
- **Three MSIs is what "agent-only" means.** Each MSI is independently installable, upgradable and removable with its own `UpgradeCode`. The bundle presents them as one product; an agent-only install simply installs one of the three. Nothing in the agent MSI knows the other two exist.
- **Burn chains prerequisites the MSIs cannot.** The .NET 10 runtime/hosting bundle is a bundle-level `ExePackage`, installed once, before any MSI. An MSI cannot install another installer.
- **The BA itself must not need .NET 10.** It runs *before* the .NET 10 runtime is installed. Target the BA at **.NET Framework 4.7.2** (inbox on every supported Windows 10/11 and Server 2019+), not .NET (Core). WiX 5 supports both hosts; the Framework one has no bootstrap-the-bootstrapper problem.
- **The MSIs stay dumb.** Files, `ServiceInstall`/`ServiceControl`, a config file, an environment-free service. All decisions arrive as properties the BA sets. That is also what makes silent install (§10) trivial: the same properties on the command line.

What the MSIs never do: run migrations, call HTTP, or store a password anywhere but the SCM. Those are BA responsibilities or deliberately out of scope (§8).

---

## 3. Install types

The first real page. One radio group; it decides which MSIs are in the chain and which configuration pages appear.

| Install type | Installs | Typical machine |
|---|---|---|
| **Server** | Control Plane + Portal, optionally a local Agent | The one box that hosts enList |
| **Agent only** | Agent | Every machine that will *run* applications |
| **Custom** | Any combination | Split tiers (portal on a DMZ host, control plane inside), or adding a component later |
| **Demo** | Everything on this machine, seeded with the sample applications and run as a session launcher rather than as services | A presenter or evaluator laptop. Designed separately in [Demo-Install-Design.md](Demo-Install-Design.md) |

Selecting a component adds its configuration page(s) to the flow (§5). "Custom" with all three unticked disables *Next*.

---

## 4. Prerequisites and detection

Detected by the BA on the **Prerequisites** page, shown as a table with a *Re-check* button, *before* any configuration page — so every later page already knows what the machine can do.

| Prerequisite | Needed by | How detected | If missing |
|---|---|---|---|
| **.NET 10 ASP.NET Core Runtime** (Hosting Bundle) | Control Plane, Portal | Registry `HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.AspNetCore.App` | Bundle installs it (`ExePackage`, chained, silent) |
| **.NET 10 Runtime** | Agent | same, `Microsoft.NETCore.App` | Bundle installs it |
| **.NET Framework 4.7.2** | Agent, **only** for `net472` applications (the legacy runner) | Registry `Release` ≥ 461808 | Informational. Agent still installs; the page notes that net472 policies will fail on this agent until it is present |
| **WSL 2.9.11+ with `wslc`** | Agent container isolation (`--container-engine wslc`); container hosting of CP/Portal | `%ProgramFiles%\WSL\wslc.exe` exists **and** `wslc version` ≥ 2.9.11 **and** `wslc list` succeeds (the service answers) | Not installable by us (`wsl --update` needs the Store and an interactive session). Show status + the command; container options on later pages are disabled |
| **Docker Desktop (Linux engine)** | Alternative container engine | `docker.exe` on PATH **and** `docker version --format {{.Server.Version}}` succeeds (daemon up, Linux engine) | Same treatment as wslc. Either engine enables container options |
| **SQL Server** | Control Plane | Not here — tested on the Database page (§6.5) against the connection the user enters | — |

Two rules that keep this honest:

- **Container support is never a requirement to install the agent.** An agent without an engine is a perfectly good process-only agent; the control plane already reports its lack of container capability on the Agents tab, and a container-mode policy targeting it fails *that application* with a clear reason. The installer says this on the page and moves on.
- **Detection is a snapshot, not a promise.** Both engines can be up at install time and down at 3 a.m. The agent re-probes on every heartbeat (`ContainerEngineStatus`); the installer only decides the *initial* `--container-engine` value.

---

## 5. Wizard flow

```mermaid
flowchart TD
    W[Welcome] --> L[License]
    L --> T[Install type]
    T --> P[Prerequisites<br/>detect .NET, wslc, Docker]
    P -->|Control Plane selected| CP[Control Plane<br/>hosting · paths · listen · account]
    CP --> DB[Database<br/>connection · test · schema]
    P -->|Portal selected| PO[Portal<br/>hosting · paths · listen · CP URL · account]
    DB --> PO
    P -->|Agent selected| AG[Agent<br/>paths · name · tags · CP URL + Verify<br/>container engine · account]
    PO --> AG
    CP -.->|no Portal| AG
    DB -.->|no Portal, no Agent| R
    PO -.->|no Agent| R[Ready to install<br/>summary]
    AG --> R
    R --> I[Installing…]
    I --> F[Finished<br/>start services · open portal]
```

Pages appear only for selected components, in the order Control Plane → Database → Portal → Agent, so a locally-installed control plane can prefill the portal's and agent's *Control plane URL* with `http://localhost:5293` before those pages are reached.

---

## 6. Page specifications

Each page lists its fields, defaults, validation, and the **property** it sets (the same name silent install uses, §10). Passwords are `Hidden` properties: never logged, never echoed in the MSI log, never written to disk (§8).

### 6.1 Welcome, 6.2 License

Standard. License is the product licence text; *Next* requires acceptance.

### 6.3 Install type

See §3. Property `INSTALLTYPE` = `Server | AgentOnly | Custom`; with `Custom`, `ADDLOCAL` lists the MSIs.

### 6.4 Control Plane

```
 ┌─ Control Plane ──────────────────────────────────────────────────────┐
 │ Hosting        (•) Windows service   ( ) Container  [wslc ▾]         │
 │                                     container options need wslc or   │
 │                                     Docker — detected: wslc 2.9.11   │
 │ Install to     [C:\Program Files\enList\ControlPlane        ] [...]  │
 │ Data           [C:\ProgramData\enList\ControlPlane          ] [...]  │
 │                package blobs, logs, appsettings                       │
 │ Listen on      [https://+:5293                              ]        │
 │                 ✓ port 5293 is free   Certificate [Select...]        │
 │ Service account (•) Network Service  ( ) This account:               │
 │                 user [                 ]  password [        ] [Test] │
 └──────────────────────────────────────────────────────────────────────┘
```

| Field | Default | Validation / behaviour | Property |
|---|---|---|---|
| Hosting | Windows service | *Container* enabled only when an engine was detected (§4); engine dropdown lists the detected ones | `CP_HOSTING` = `Service | Container`, `CP_ENGINE` = `wslc | docker` |
| Install to | `%ProgramFiles%\enList\ControlPlane` | Writable, not a system directory; hidden in Container mode (nothing is installed there) | `CP_INSTALLDIR` |
| Data | `%ProgramData%\enList\ControlPlane` | Writable. Becomes `PackageStorage:Root` (`\PackageBlobs`) and the home of `appsettings.json`. In Container mode it is the bind-mounted volume | `CP_DATADIR` |
| Listen on | `https://+:5293` | Parses as a URL prefix; port not currently bound (`GetActiveTcpListeners`). This is what goes into the service's `--urls` — a service has no shell, so the flag in `binPath` is how it learns its address (Deployment-IaC §1.6). **`https`, and the page must collect a certificate with it.** `+` is not loopback, so the control plane refuses to start on plain `http` there: authentication defaults to `Required` and `Required` will not carry credentials in the clear off loopback ([Authentication-Design.md §9](../03-architecture/Authentication-Design.md)). A `http://+:5293` default — which this page carried until 2026-09-13 — produces a service that installs cleanly and then never starts | `CP_URLS`, `CP_CERT` |
| Service account | Network Service | *This account*: `LogonUser` test on *Test*; the MSI grants *Log on as a service* and write access to `CP_DATADIR`. Hidden in Container mode | `CP_ACCOUNT`, `CP_PASSWORD` (Hidden) |

### 6.5 Database (Control Plane only)

```
 ┌─ Database ───────────────────────────────────────────────────────────┐
 │ Server         [sql01.corp.local,1433                       ]        │
 │ Database       [EnlistControlPlane                          ]        │
 │ Authenticate   (•) as the service account (Windows)                  │
 │                ( ) SQL login   user [        ] password [        ]   │
 │                [ Test connection ]  ✓ connected · schema not present │
 │ Schema         (•) Create or upgrade it now (runs the migrations     │
 │                    bundle under your elevated credentials)           │
 │                ( ) A DBA will apply it — the service will not start  │
 │                    until the schema exists                           │
 └──────────────────────────────────────────────────────────────────────┘
```

| Field | Validation / behaviour | Property |
|---|---|---|
| Server, Database | *Test connection* opens a `SqlConnection`, runs `SELECT 1`, then checks for `__EFMigrationsHistory` and reports **present / not present / behind by N** so the Schema choice is informed | `DB_SERVER`, `DB_NAME` |
| Authenticate | Windows: the connection string gets `Integrated Security=True` and the BA reminds the user to grant the service account on the server (it cannot do that itself). SQL login: password Hidden | `DB_AUTH`, `DB_USER`, `DB_PASSWORD` (Hidden) |
| Schema | *Create or upgrade now* runs the **EF Core migrations bundle** (`efbundle.exe`, shipped in the MSI) from the BA, elevated, against the entered connection — the production path Deployment-IaC §1.4 describes, so the control plane itself never migrates. *A DBA will apply it* skips this; the Finish page then says plainly that `enlist-controlplane` will refuse to start (Production verifies the schema) until it is done | `DB_MIGRATE` = `1 | 0` |

*Test connection* is required before *Next* in either case: an unreachable database is the most common failed install, and this is the cheapest place to find out.

### 6.6 Portal

Same layout as §6.4 (hosting, install path, data path for `appsettings.json` and logs, listen `https://+:5231`, service account) plus:

| Field | Default | Validation / behaviour | Property |
|---|---|---|---|
| Control plane URL | `http://localhost:5293` when the Control Plane is in this install, else empty | *Verify* as on the Agent page (§6.7). Becomes `ControlPlane:BaseUrl` in the portal's `appsettings.json` | `PORTAL_CPURL` |

The portal never touches SQL, so there is no database page for it.

### 6.7 Agent

```
 ┌─ Agent ──────────────────────────────────────────────────────────────┐
 │ Install to     [C:\Program Files\enList\Agent               ] [...]  │
 │ Data           [C:\ProgramData\enList\Agent                 ] [...]  │
 │                package cache, staged runners, logs                    │
 │ Agent name     [WEB-07                                      ]        │
 │ Tags           [env=prod  role=web                          ] (opt)  │
 │ Control plane  [https://enlist.corp.local:5293              ] [Verify]│
 │                 ✓ reachable — 14 agents registered                   │
 │ Containers     ( ) None — process isolation only                     │
 │                (•) wslc 2.9.11 (detected)   ( ) Docker (not found)   │
 │                Image  [enlist/runner:3.0.0                  ]        │
 │ Service account (•) Local System  ( ) This account: [...]            │
 └──────────────────────────────────────────────────────────────────────┘
```

| Field | Default | Validation / behaviour | Property |
|---|---|---|---|
| Install to | `%ProgramFiles%\enList\Agent` | Writable. Holds the agent, both runners (`enlist-runner`, and the net472 `enlist-runner.exe` for legacy applications) | `AGENT_INSTALLDIR` |
| Data | `%ProgramData%\enList\Agent` | Becomes `--data`: `Packages\`, `Runners\`, `Logs\` (the layout `.demo/README.md` documents) | `AGENT_DATADIR` |
| Agent name | machine name | Must be unique in the fleet — **two agents with one name silently corrupt each other's reports** (Runbook). The duplicate is caught by **enrollment itself**, which answers `409` for a name already holding a live credential, and the installer surfaces that as *this name is taken; revoke the old agent's credential first, or pick another*. It must NOT be checked with `GET /api/agents/{name}` as this page said until 2026-09-13: that endpoint is Viewer-policed, the installer holds at most a join token at that moment, and the call answers `401` — so the warning silently never fired on exactly the deployments that have authentication turned on | `AGENT_NAME` |
| Tags | empty | `key=value` pairs; applied with `PUT /api/agents/{name}/tags` on first start — policies target agents by tag, so an agent installed with `env=prod` is schedulable the moment it registers | `AGENT_TAGS` |
| Control plane URL | prefilled if CP is in this install | **Verify** (BA, `HttpClient`, 5 s timeout): `GET {url}/health` → 200 whose body names `enList control plane` = *reachable — enList control plane {version}, database healthy*; 503 with that same body = *reachable, but its database is down* (allowed, with a warning); a connection error or a non-JSON body = *not an enList control plane*. Verification is **required** to proceed, with one escape hatch: *Continue anyway* for machines imaged before the control plane exists — recorded in the summary and on the Finish page | `AGENT_CPURL` |
| Containers | the detected engine, else *None* | Radios enabled per §4 detection. Sets `--container-engine` and `--container-image`. Note under the group: *net472 applications cannot run in a container* (Deployment-IaC). **`enlist/runner:3.0.0` is a proposed RELEASE tag and does not exist yet** — the repository builds `enlist/runner:dev` and `enlist/runner:wslc`. Publishing a version-tagged image is an open item (§12) and the installer cannot ship before it is settled, since this field's default has to name something pullable | `AGENT_ENGINE` = `none | wslc | docker`, `AGENT_IMAGE` |
| Service account | Local System | Local System is the pragmatic default: the agent starts child runners, enrols them in a Job Object, and (with an engine) drives `wslc`/`docker`, all of which want a broad host account. *This account* is offered for locked-down hosts, with the same *Test* | `AGENT_ACCOUNT`, `AGENT_PASSWORD` (Hidden) |

The resulting service is `enlist-agent`, `binPath` = `"…\enlist-agent.exe" --control-plane {AGENT_CPURL} --agent {AGENT_NAME} --runner-bin "…\runner" --legacy-runner-bin "…\runner-legacy" --data "{AGENT_DATADIR}" [--container-engine {AGENT_ENGINE} --container-image {AGENT_IMAGE}]` — exactly the flags [`demo/start-demo.ps1`](../../demo/start-demo.ps1) passes today, so the installer and the demo cannot drift apart on what an agent needs.

### 6.8 Ready to install

A read-only summary of every choice above (passwords shown as `••••`), including anything the user overrode with *Continue anyway*. This is the page a screenshot of goes into a ticket.

### 6.9 Installing, 6.10 Finished

Progress per package. Finish offers *Start services now* (checked) and *Open the portal* (`http://localhost:5231`, only when the portal was installed). If the schema was skipped (§6.5) or verification was overridden (§6.7), Finish repeats that in red — those are the two things that will make the fleet look broken an hour later.

---

## 7. "As a service" and "in a container" — what each actually does

**Windows service** is the well-trodden path and matches Deployment-IaC §1.6 exactly: the MSI lays down the published app, `ServiceInstall` creates `enlist-controlplane` / `enlist-portal` / `enlist-agent` with `binPath` carrying `--urls` (or the agent flags), `ServiceControl` starts it, and configuration lives in `%ProgramData%\enList\<component>\appsettings.json` with an ACL of *SYSTEM + Administrators + the service account*. Environment-variable overrides are deliberately not used — a service has no shell to export into.

**Container** hosting of the control plane or portal is not one thing but three, and it is worth being explicit because none of it exists yet:

1. **An image for each.** Only the *runner* has a Dockerfile today. The control plane and portal need their own (`mcr.microsoft.com/dotnet/aspnet:10.0`, publish output, `--urls https://+:5293`, for the same reason §6.4 does). The MSI would carry each image as a `docker save`/`wslc save` tarball and `load` it, so an install needs no registry access.
2. **Provisioning.** The BA creates the container: published port (`0.0.0.0:5293:5293` — a hosted control plane must be reachable from other machines, unlike the loopback-only runner control channel), the data directory as a volume, configuration as `--env` (`ConnectionStrings__ControlPlane`, `ControlPlane__BaseUrl`).
3. **A host-side supervisor service.** `wslc` has **no restart policy** (Docker's `restart: unless-stopped` has no equivalent — learned the hard way on the demo, [`demo/sql-server/README.md`](../../demo/sql-server/README.md)), so a container hosted this way is simply stopped after every reboot. The MSI therefore installs a tiny service, `enlist-<component>-host`, whose whole job is `wslc start`/`docker start` at boot and `stop` on shutdown. With Docker the supervisor is redundant but harmless, so ship one shape.

Two consequences the Database page has to respect: a control plane *in* a container reaches SQL Server from the container's network, so `localhost` on the Server field means the container, not the host (the BA rewrites `localhost` to the host address for the container's env, and says so); and Windows authentication to SQL Server is unavailable from a Linux container, so *SQL login* becomes the only Authenticate option when Container hosting is selected.

**Recommendation:** build the installer with **Service** hosting for the control plane and portal in v1 and keep **Container** hosting behind the same UI as a v2 item — the radio is present and disabled with "coming in a later release", so the page shape does not change when it lands. The three pieces above are real work (two Dockerfiles, an image-distribution story, and the supervisor pattern, which the demo's `demo-db.ps1 up` is already a working prototype of), and none of them should gate shipping an agent installer, which is the one every machine needs.

---

## 8. Credentials — what "login" can and cannot mean today

Three different things get called a login on an installer page. Only two of them exist in this product.

| Credential | Exists today? | Where the installer puts it |
|---|---|---|
| **Service logon account** — the Windows identity each service runs as | Yes | Collected per component (§6.4, §6.6, §6.7), validated with `LogonUser`, applied through `ServiceInstall` `Account`/`Password`. The password is a Hidden property: it reaches the SCM and nothing else — not the MSI log, not a config file, not the registry |
| **Database credentials** — how the control plane reaches SQL Server | Yes, and **not LocalDB** — see the note below §12.9 | Windows auth via the service account (preferred; nothing stored) or a SQL login written into the ACL-restricted `appsettings.json`. If SQL auth must be used, the connection string should be **DPAPI-protected at machine scope** rather than plaintext — a small addition to the control plane's configuration loading that is worth doing before an installer ships it |
| **An application login for the portal / control plane** — a user and password to sign in to enList | **No, by design** — and never will be. People sign in with **Windows** (built 2026-09-11, [Authentication-Design.md §5](../03-architecture/Authentication-Design.md)); the installer collects two group names, not a password. Machines enroll with a join token; tools get API keys. | The Portal page's Operators and Viewers group fields; the Agent page's join-token field; the portal's key, minted with `create-api-key --name portal` and stored with `Enlist.Portal.exe protect`. |

So for requirement 2's "provide a login for the portal / control plane if they are installed as a service": that is the **service logon account**, and it is designed in.

**There is no initial-administrator page, and there will not be one.** Authentication was built on
2026-09-11 ([Authentication-Design.md §14](../03-architecture/Authentication-Design.md) lists the
page changes), and it removed the need rather than filling it: people sign in with Windows, so the
Portal page asks for the Operators and Viewers group names instead of a password. Concretely:

- The **Agent page** gains a Join token field (`AGENT_JOINTOKEN`), which the installer exchanges for
  the agent's credential before the service is ever **started** — the token must never end up in the
  service's `binPath`, where any local user can read it out of the process list.

  *(Built 2026-09-13. This said "before the service is created", which is not what happens and could
  not: the MSI creates the service. What it is actually about is the `binPath`, and that is honoured
  exactly — `AGENT_JOINTOKEN` is a bundle variable that no package references, so nothing passes it
  to an MSI. The order is: the MSI creates the service **stopped**, the bootstrapper runs
  `enlist-agent enroll` once with the token, and the credential is on disk before anything starts.
  The service is never running without one, which is the property that was wanted.)*
- The **Control Plane and Portal pages** gain a TLS certificate, because authentication is `Required`
  by default and `Required` refuses plain HTTP off loopback. This is not a nicety: without it the
  listen defaults on those pages produce services that install and then never start.
- The installer mints the portal's own key with the control plane's `create-api-key` verb and stores
  it DPAPI-protected with `Enlist.Portal.exe protect`.

  *(Built 2026-09-13, with one addition the design did not anticipate. `create-api-key` writes to the
  database directly, so on a fresh machine there is no schema for it to write to — and the control
  plane deliberately refuses to migrate itself outside Development, for permissions reasons. The
  installer therefore runs `Enlist.ControlPlane.exe apply-schema` first: the named, deliberate deploy
  step that Deployment-IaC §1.4 already called for, now with a verb. The portal's key is only minted
  where the control plane is installed too; a split-tier portal is told on the Finish page that an
  Operator must supply one, because the key cannot be created from a machine with no database.)*

*(This section previously said the administrator page "appears only once C2 is implemented" and that
the Finish page would meanwhile carry a trusted-network warning. Both were written before the
decision and neither survived it; the paragraph sat above its own correction for two days, which is
long enough for someone to read the first half and stop.)*

---

## 9. Install paths and on-disk layout

Binaries under Program Files, everything that changes under ProgramData, per component, so an upgrade can replace the former and must never touch the latter.

| | Control Plane | Portal | Agent |
|---|---|---|---|
| Binaries | `%ProgramFiles%\enList\ControlPlane` | `%ProgramFiles%\enList\Portal` | `%ProgramFiles%\enList\Agent` (+ `runner\`, `runner-legacy\`) |
| Configuration | `%ProgramData%\enList\ControlPlane\appsettings.json` | `%ProgramData%\enList\Portal\appsettings.json` | none — the agent is configured entirely by service arguments |
| Data | `…\ControlPlane\PackageBlobs` (`PackageStorage:Root`) | — | `…\Agent\{Packages,Runners,Logs}` (`--data`) |
| Logs | `…\ControlPlane\Logs` | `…\Portal\Logs` | `…\Agent\Logs` |
| Service | `enlist-controlplane` | `enlist-portal` | `enlist-agent` |

Every path is overridable on its page; the defaults are what `sc.exe` deployments have used, just under `%ProgramFiles%` instead of `C:\enlist`.

---

## 10. Silent / unattended install

The same properties, on the command line. This is the surface Deployment-IaC §4's proposed IaC would call, and the reason the UI sets properties rather than doing work itself.

```powershell
# Server: control plane + portal as services, migrate now, Windows auth to SQL
enList-3.0.0-Setup.exe /quiet /log install.log `
  INSTALLTYPE=Server `
  CP_URLS=https://+:5293 CP_ACCOUNT="NT AUTHORITY\NetworkService" `
  DB_SERVER=sql01.corp.local DB_NAME=EnlistControlPlane DB_AUTH=Windows DB_MIGRATE=1 `
  PORTAL_URLS=https://+:5231 PORTAL_CPURL=https://localhost:5293

# Agent only, on a machine with wslc
enList-3.0.0-Setup.exe /quiet /log install.log `
  INSTALLTYPE=AgentOnly `
  AGENT_NAME=WEB-07 AGENT_TAGS="env=prod role=web" `
  AGENT_CPURL=https://enlist.corp.local:5293 `
  AGENT_ENGINE=wslc AGENT_IMAGE=enlist/runner:3.0.0
```

Rules: verification still runs in silent mode and **fails the install** on an unreachable control plane or database unless `AGENT_CPURL_SKIPVERIFY=1` / `DB_SKIPTEST=1` is passed explicitly — the same escape hatches as the UI's *Continue anyway*, spelled out so a script cannot skip them by accident. Passwords on a command line are visible in process listings; the documented alternative is a response file (`/response install.rsp`) with an ACL.

---

## 11. Upgrade, repair, uninstall

- **Upgrade:** major upgrades (stable `UpgradeCode` per MSI, new `ProductCode` per version). Services are stopped, binaries replaced, `%ProgramData%` untouched, services started. If `DB_MIGRATE=1` was chosen originally the bundle re-runs the migrations bundle on upgrade — the control plane will refuse to start against an old schema otherwise. Agent upgrade is the one that must be smooth, since it is the fleet-wide one: stop, replace, start; the agent's package cache and staged runners survive, running applications restart under the new agent.
- **Repair:** re-lays binaries and re-registers services; never touches configuration or data.
- **Uninstall:** removes binaries and services, **keeps** `%ProgramData%\enList` by default; a *Remove all data* checkbox on the confirmation page (and `REMOVEDATA=1` silently) deletes it. Uninstalling an agent does not deregister it from the control plane — a machine being rebuilt should keep its registration; the portal's Agents tab is where an operator retires one deliberately.

---

## 12. Open decisions

These need a call before WiX is written. My recommendation is in bold.

1. **Container hosting of the control plane and portal in v1 or v2?** **v2** — see §7. It needs two Dockerfiles, an image-distribution story and the supervisor service; the agent installer is the one every machine needs and it should not wait.
2. **Default service accounts.** **Network Service for the control plane and portal, Local System for the agent** (§6.7 says why). Group-managed service accounts (`gMSA`) should be accepted in the *This account* field (name ending `$`, no password) — cheap to support and what a domain will want.
3. **Who applies the schema by default?** **The installer, when the person running it has the rights** (`DB_MIGRATE=1` default in the UI), with the DBA path one radio away. The migrations bundle keeps the control plane itself from ever migrating, which is the property Deployment-IaC §1.4 cares about.
4. **Add `GET /health` to the control plane.** **Done (2026-09-11).** `GET /health` reports product, version and database reachability, 200 when the database answers and 503 when it does not — see [API-Specification §5b](../03-architecture/API-Specification.md). *Verify* on the Agent and Portal pages targets it, and so should service monitors.
5. **Where do runner images come from for an agent install?** ~~Bundled tarball, loaded at install.~~ **Decided 2026-09-14: a separate download that operators side-load themselves** (`docker load -i` / `wslc load -i`) into whichever engine, on whichever machines, they run container-mode agents. The installer does not carry the image and does not load it, so it is ~200 MB smaller than a bundled image would have made it, and an agent that runs applications only as processes never handles the image at all.
   What follows from it: the agent runs every container with `--pull never` (since 2026-09-14; item 8 says why), so an agent whose machine has not had the image loaded reports it missing by name rather than fetching anything. The download is the image `installer\build.ps1` builds, `enlist/runner:<product version>`, saved into `out\` beside the installers as `enlist-runner-<version>.tar` (about 80 MB) with a `.sha256` in the format `sha256sum -c` reads; `verify.ps1` checks the file loads under the tag the agent package names and was built from the current runner source. An `enlist-runner-<version>-README.md` beside it gives the two load commands. The wizard says it as well (since 2026-09-14): typing a container engine on the Agent page replaces the "leave it empty" hint with an amber note naming the image and the load command, and the Finish page repeats it in full after a successful install. The words are `InstallPlan.RunnerImageNote`, so they are tested and follow the engine and image as typed.
6. **Authentication (C2).** **Decided and built (2026-09-11).** The pages change as [Authentication-Design.md §14](../03-architecture/Authentication-Design.md) lists: the Portal page asks for the Operators and Viewers group names and a TLS certificate, the Control Plane page a certificate, the Agent page a join token (the installer enrolls and writes the credential file), and the installer mints the portal's key with `create-api-key` and stores it with `Enlist.Portal.exe protect`. No initial-administrator page.
7. **Code signing.** The bundle and all three MSIs should be Authenticode-signed; an unsigned installer that creates three services is what a SmartScreen warning is for. Needs a certificate decision, not a design one.
8. **What tag does a released runner image carry?** **`enlist/runner:<product version>`, i.e. `enlist/runner:3.0.0`** — which is what §6.7 and the silent example assume. **Built since 2026-09-14:** `installer\build.ps1` builds the image under that tag, labelled with the version and a hash of the source it came from, and `verify.ps1` checks the tag the agent package names exists, carries that version, and is current. Until then nothing produced it, so every container-mode agent pointed at an image that existed nowhere.
   That was worse than a failure. Both engines default to pulling a missing image, and `enlist/runner` on Docker Hub is not a namespace enList owns — a machine without the image would have fetched and run whatever was published under that name, as LocalSystem, with an application package mounted inside. **The agent now runs every container with `--pull never`** and reports a missing image as the installation problem it is.
   What remains is getting the image onto an agent's machine: a download operators side-load, per item 5.
9. **TLS certificates for the control plane and portal.** ~~Not designed yet.~~ **Decided and built (2026-09-13): machine store by thumbprint, with the PFX path as the fallback.**

   Both pages have a certificate picker (`CP_CERT`, `PORTAL_CERT`), and the thumbprint goes on the service command line as `--Certificate:Thumbprint` — allowed precisely because a thumbprint *names* a certificate rather than granting access to one. A PFX password never goes near it, for the same reason the join token and the SQL login do not.

   Three things the design did not anticipate, each of which would otherwise have produced a service that installs cleanly and then fails:

   - **Thumbprint is not something ASP.NET Core can do for itself.** Its built-in `Kestrel:Certificates:Default` takes a PFX path or a store lookup by *subject*, and a subject is not unique — two certificates for the same host, one expired, is an ordinary state of affairs. `Enlist.ControlPlane.Contracts.ServerCertificate` resolves by thumbprint; Kestrel's own configuration still works and is left alone.
   - **Reading a certificate and reading its private key are different permissions.** The key is a file under `%ProgramData%\Microsoft\Crypto` whose ACL names only whoever imported it, so a certificate installed by an administrator and served by `NETWORK SERVICE` — the default for both hosts — binds and then fails every handshake. The bootstrapper grants it with `grant-certificate-access`, before the service starts.
   - **`netsh http add sslcert` is not an option here**, though this list offered it. That binds a certificate for **http.sys**, and both hosts are Kestrel.

   The picker shows the friendly name, the subject, the expiry and the last eight of the thumbprint, because a machine store routinely holds several certificates that are identical in every other column — two `CN=localhost` entries both named "ASP.NET Core HTTPS development certificate", differing only in that one expires shortly, is what the machine this was built on actually has. A certificate within 60 days of expiry is offered with a warning rather than refused.

   **Development is exempt from the startup rule**, because that is where `dotnet dev-certs https` lives and Kestrel uses it automatically — the same split, for the same kind of reason, as the migration check.

10. **The Database page must not offer LocalDB, and has no default server.** *(Found by installing for real, 2026-09-13.)*

    The Database page prefilled `(localdb)\MSSQLLocalDB`, to save the operator filling in a box on a page they had no reason to touch. That instinct is right and the answer was wrong: **a LocalDB instance belongs to the account that starts it.** The installer applies the schema as the elevated operator, creating the database in *that* operator's instance; the service then starts as `NETWORK SERVICE` or `LocalSystem`, asks for the same instance name, and gets an empty one of its own. The control plane correctly refuses to create a schema outside Development, and the operator is left with

    > Cannot connect to the control plane database.

    — after an install that reported success. That reads like a network or permissions problem and is a long way from the actual mistake.

    Three changes, because one of them alone leaves a hole:

    - **`DB_SERVER` has no default.** With none given, `Enlist.ControlPlane.msi` writes *no connection string at all* — exactly as it already does for a SQL login — and the control plane's own refusal is what the operator reads. There is no honest default for "where is your SQL Server"; `DB_NAME` keeps one because a database name is a naming choice rather than a fact about someone's estate.
    - **The control plane refuses to be pointed at LocalDB by a service** (`DatabaseRules`). `Program.cs` already refused to *fall back* to LocalDB outside Development; supplying the string from the MSI walked straight past that check. This is the other half of the same decision. A developer at a terminal is unaffected — LocalDB is exactly right there, and the rule is about the identity rather than the database.
    - **The wizard blocks the page**, so it is caught before the install rather than at first start.

    Prefilling a value that cannot work is worse than an empty box: it is an empty box the operator does not know to look at.

---

*Related:* [`Deployment-IaC.md`](Deployment-IaC.md) (current manual deployment; §4 the IaC shape this installer's silent mode serves) · [`Runbook.md`](Runbook.md) (the duplicate-agent-name failure §6.7 guards against) · [`demo/README.md`](../../demo/README.md) (the demo's `start-demo.ps1` is the living reference for what each service is started with).
