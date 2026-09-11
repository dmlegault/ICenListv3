# Software Requirements Specification (SRS)

**Product:** enList v3
**Document status:** Derived from the current implementation as of 2026-09-03. See [`BRD.md`](BRD.md) for business context and [`FRS.md`](FRS.md) for feature-level functional detail.

---

## 1. Introduction

### 1.1 Purpose

This document specifies the software requirements for enList v3: a control plane, agent, runner, deployment CLI, and web portal that together deploy and supervise Windows-hosted applications (services and scheduled jobs) across a fleet of machines.

### 1.2 Scope

Covers five components, all under this repository:

| Component | Path | Role |
|---|---|---|
| Enlist.ControlPlane | `src/Enlist.ControlPlane` | Central REST API + SignalR hub + SQL Server-backed store of agents, application policies, packages, status reports, logs. |
| Enlist.Agent | `src/Enlist.Agent` | Runs on every managed machine; reconciles desired state; supervises Runner child processes. |
| Enlist.Runner | `src/Enlist.Runner` | One process per running application instance; discovers and hosts plugin services/jobs. |
| Enlist.Deploy | `src/Enlist.Deploy` | CLI (`enlist-deploy`) that packages and uploads an application. It does NOT place it — see SRS-ASG-1. |
| Enlist.Portal | `src/Enlist.Portal` | Blazor Server web UI for fleet-wide visibility and control. |

### 1.3 Definitions

| Term | Meaning |
|---|---|
| **Application** | A named unit of deployment, made up of zero or more Services and zero or more Jobs, identified by `ApplicationName`. |
| **Service** | A long-running component with `[EnlistStart]`/optional `[EnlistStop]` methods, started when its application starts and running until stopped. |
| **Job** | A scheduled or on-demand unit of work with an `[EnlistExecute]` method and an optional cron expression. |
| **Application Policy rule** | A row declaring that one Application should run (`Running`) or not (`Stopped`) on whichever agents a tag selector currently matches. Formerly called an *Assignment*. |
| **Machine** | One registered managed host, identified by name, carrying a set of key/value tags. |
| **Package** | A content-addressed (SHA-256 digest) zip of an application's build output, stored once and reused across every machine it is assigned to. |
| **Desired state** | The persisted intent (`Running`/`Stopped`) an Assignment carries — what the agent is supposed to converge its machine toward. |
| **Reported state** | What an agent's latest status snapshot says is actually happening right now. |

### 1.4 References

- [`docs/03-architecture/enList-v3-Design.md`](../03-architecture/enList-v3-Design.md) — the original design document this whole system was built against; the requirements below are the shipped consequence of it, and it remains authoritative on rationale.
- [`docs/04-requirements/BRD.md`](BRD.md) — business objectives and scope.
- [`docs/03-architecture/SAD.md`](../03-architecture/SAD.md) — system architecture and component interaction, including a design-vs-implementation delta section.
- [`docs/03-architecture/Database-Design.md`](../03-architecture/Database-Design.md) — full schema.
- [`docs/03-architecture/API-Specification.md`](../03-architecture/API-Specification.md) — full REST/SignalR contract.
- `contracts/EnlistAttributes.cs` — the plugin authoring contract (source of truth for plugin-facing requirements).

---

## 2. Overall Description

### 2.1 Product Perspective

enList v3 is a new, standalone system (not a plugin or extension of another product), though it is a rewrite whose UI branding and some design choices are explicitly carried over from a prior internal tool referred to in code comments as "enList v2." It has no dependency on that prior system at runtime.

### 2.2 Product Functions (summary)

1. Register and tag machines.
2. Package and upload an application's build output; store it deduplicated and versioned.
3. Declare desired state for an application on whichever agents a tag selector currently matches. There is no second, "explicit" targeting mode: every agent carries an implicit self-tag `{"agent": name}`, so targeting one specific agent is simply the most specific possible selector.
4. Reconcile that desired state on each managed machine: start/stop application processes, register/unregister scheduled jobs.
5. Report live status (per-application, per-service, per-job) back to the control plane, both reactively and on a fixed heartbeat.
6. Forward application log output for centralized tailing.
7. Issue imperative start/stop commands against one service or job without affecting the rest of its application — stopping a job both unregisters its schedule and cancels any run in flight.
8. Present all of the above in a web portal: three tabs (Applications, Agents, Logs), with per-application policy and packages manager, and a live Logs tail.

### 2.3 User Classes

| Class | Description |
|---|---|
| Operator | Uses Enlist.Portal to deploy, start/stop, inspect, and roll back applications across the fleet. |
| Plugin author | Writes Service/Job classes against the `Enlist` attribute contract; does not interact with the control plane directly. |
| Automation / script | Drives `enlist-deploy` or the REST API directly (e.g., from a CI pipeline) instead of the portal. |

### 2.4 Operating Environment

- **Runtime:** .NET 10 on every component.
- **OS:** Windows for every managed machine (agent + runner); the control plane and portal are ASP.NET Core and are Windows-hosted in this deployment but have no OS-specific code themselves.
- **Database:** SQL Server (LocalDB acceptable for development; see `ConnectionStrings:ControlPlane`).
- **Browser:** any modern evergreen browser for Enlist.Portal (Blazor Server, MudBlazor 8.6.0 components).

### 2.5 Design and Implementation Constraints

- **Agents dial out only.** No component ever opens an inbound listener on a managed machine for enList's own traffic; agents connect outward to the control plane over HTTP and a SignalR hub connection.
- **Zero shared compiled assembly between Runner and a plugin.** The plugin attribute types are matched by simple type **name** (`CustomAttributeData.AttributeType.Name`) and expected namespace (`Enlist`), never by `is`/`IsAssignableFrom` against a referenced type — see `contracts/EnlistAttributes.cs`'s own header comment for the rationale (a plugin project must never take a `PackageReference`/`ProjectReference` on a compiled `Enlist.Contracts` assembly).
- **Content-addressed package storage.** A package's identity is the SHA-256 digest of its bytes, computed server-side; re-uploading identical bytes is a no-op.
- **Opaque status payload.** The control plane stores an agent's status report as an unparsed JSON blob (`AgentReportEntity.SnapshotJson`) rather than a normalized schema, so the agent-side status shape can evolve without a database migration.

### 2.6 Assumptions and Dependencies

- Authentication is implemented on the control plane (bearer tokens, `Required` by default, `Off` only on loopback — [Authentication-Design.md](../03-architecture/Authentication-Design.md)); the agent and portal sides are not yet, so a deployment with clients runs on loopback (see [`BRD.md` §7](BRD.md#7-constraints-and-assumptions)).
- Cron expressions are parsed and scheduled using the Cronos library (`Enlist.Agent.csproj` → `Cronos 0.11.0`).

---

## 3. System Features (requirement-level)

Each requirement is tagged `SRS-<area>-<n>` and is implemented and exercised by the test(s) named where one exists.

### 3.1 Machine Registry

- **SRS-MCH-1**: The system SHALL create a machine record the first time that machine's agent calls `GET /api/agents/{agentName}/policies` or `POST /api/agents/{agentName}/report`, if no record already exists.
- **SRS-MCH-2**: The system SHALL allow a machine to be pre-registered with tags (`PUT /api/agents/{name}/tags`) before its agent ever connects.
- **SRS-MCH-3**: The system SHALL update a machine's `LastSeenUtc` on every policy-fetch and every status report it sends.
- **SRS-MCH-4**: *(withdrawn)* — deleting an agent is a registry cleanup, not data destruction: the agent reappears on its next contact, and policy rules target tags rather than agent rows, so there is nothing for a referential check to protect. The former `409 Conflict` on delete existed only while explicit per-agent targeting did.
  *(Verified by `AgentRegistryTests`.)*

### 3.2 Placement (Assignments)

- **SRS-ASG-1**: A policy rule SHALL target agents by `TagSelector` only. There is no `AgentName` field and no explicit targeting mode: every agent carries an implicit `agent=<name>` tag, so "this one agent" is the narrowest possible selector rather than a second mechanism. *(Revised — this requirement previously mandated exactly one of `AgentName` or `TagSelector`, contradicting §2.3 of this same document.)*
- **SRS-ASG-1a**: A policy rule SHALL specify a `PackageDigest` obtained from a prior upload; `enlist-deploy` never creates one, so deploying a build and deciding where it runs remain separate acts.
- **SRS-ASG-2**: An assignment SHALL specify exactly one of a local `Path` or a `PackageDigest` as its source, never both, never neither.
- **SRS-ASG-3**: The system SHALL resolve, for a given agent, every rule whose `TagSelector` is fully satisfied by that agent's effective tags (its own, plus the implicit `agent=<name>`), then collapse the result to at most one outcome per application — agreeing rules resolve to one, disagreeing rules yield a `ConflictReason` rather than a silently-chosen winner — using one shared resolution routine (`ResolveEffectivePoliciesForAgentAsync`) for both the agent-facing and portal-facing query paths, so the two views can never disagree.
- **SRS-ASG-4**: *(withdrawn)* — the filtered unique index on `(AgentName, ApplicationName)` is gone along with explicit targeting. Two tag selectors can begin matching the same agent at any time simply because someone re-tagged it, so overlap cannot be prevented by a database constraint and is instead detected at resolution: disagreeing rules for one application yield a `ConflictReason`, and a static host-port collision between different applications yields another. See [`Container-Story.md` §8.2](../03-architecture/Container-Story.md).
- **SRS-ASG-5**: Creating, updating, or deleting an assignment SHALL push an `ApplicationPoliciesChanged` notification (no payload) to every machine that assignment affects, over the SignalR hub.

### 3.3 Reconciliation (Agent)

- **SRS-RCN-1**: On startup, and on every `ApplicationPoliciesChanged` signal (including one synthesized on hub reconnect), the agent SHALL re-fetch its full assignment list and converge: stop applications no longer wanted, start applications newly wanted, restart applications whose Path/PackageDigest/CronOverrides changed.
- **SRS-RCN-2**: The agent's SignalR connection SHALL re-join its machine's hub group after every reconnect, not only at initial connect — a connection that reconnects without rejoining receives no further pushes even though it appears healthy.
- **SRS-RCN-3**: A crashed application instance SHALL be restarted automatically with exponential backoff, up to a configured maximum attempt count, after which it is marked `Failed` and not retried further.
- **SRS-RCN-4**: Stopping an application SHALL tear down its runner process and mark every one of its previously-known services `Stopped` in the next status report — a stale `Running` service entry under a stopped application is a defect.

### 3.4 Status Reporting

- **SRS-STA-1**: The agent SHALL POST a status snapshot after every service/job state transition and after every reconciliation pass.
- **SRS-STA-2**: The agent SHALL POST a status snapshot on a fixed heartbeat interval (default 2 minutes) regardless of whether anything changed, so a healthy but quiescent agent's last-report age never falsely implies it is offline.
- **SRS-STA-3**: The portal SHALL treat a machine as stale once its most recent status report is older than a fixed threshold (5 minutes) and SHALL visually distinguish stale data from live data rather than presenting both identically.

### 3.5 Package Distribution

- **SRS-PKG-1**: The system SHALL compute a package's digest server-side from the uploaded bytes; a client-supplied digest SHALL NOT be trusted.
- **SRS-PKG-2**: Uploading bytes that already exist under their digest SHALL be a no-op that still reports success (`AlreadyExisted: true`).
- **SRS-PKG-3**: The system SHALL assign each application name its own monotonically increasing `VersionNumber`, never reused, even after older versions are garbage-collected.
- **SRS-PKG-4**: The system SHALL refuse to delete a package still referenced by any assignment (`409 Conflict`), whether that assignment's desired state is `Running` or `Stopped`.
- **SRS-PKG-5**: The system SHALL automatically garbage-collect a package only after it has been unreferenced continuously for at least the configured retention period (default 30 days), checked on a fixed sweep interval (default 6 hours).

### 3.6 Imperative Control

- **SRS-CMD-1**: The system SHALL support starting/stopping one named service, or pausing/resuming one named job, without altering the running state of anything else in that application.
- **SRS-CMD-2**: An imperative command SHALL be delivered fire-and-forget over the same SignalR connection used for assignment-changed pushes; the system SHALL NOT guarantee delivery or acknowledge success synchronously — the effect, if any, is only observable in the next status report.

### 3.7 Logging

- **SRS-LOG-1**: The agent SHALL buffer and forward application log lines to the control plane in batches, tagged by machine, application, level, source, and timestamp.
- **SRS-LOG-2**: The portal SHALL retrieve log lines for a (machine, application) pair paged strictly after the highest `Id` already retrieved, so a continuous tail never re-fetches or skips a line.
- **SRS-LOG-3**: Forwarded log rows SHALL be subject to their own retention policy (default 3 days) independent of an agent's local on-disk log files.

### 3.8 Plugin Discovery

- **SRS-PLG-1**: The runner SHALL discover Service and Job types by scanning loaded assemblies for types carrying an attribute whose **name** is `EnlistServiceAttribute`/`EnlistJobAttribute`, regardless of whether that attribute type is the exact same compiled type the runner itself references.
- **SRS-PLG-2**: A type carrying `[EnlistService]` with no `[EnlistStart]` method, or `[EnlistJob]` with no `[EnlistExecute]` method, SHALL be reported as a discovery warning rather than silently ignored or fatal to discovery of everything else in that assembly.
- **SRS-PLG-3**: An assembly-level `[EnlistApplication(Description = "...")]` attribute, if present, SHALL be surfaced end-to-end (runner → agent → control plane → portal) as the application's description; its absence SHALL NOT be an error.
- **SRS-PLG-4**: Plugin isolation SHALL use a dedicated `AssemblyLoadContext` per running application instance so two applications' private dependencies cannot collide.

### 3.9 Portal (see [`FRS.md`](FRS.md) for full page-level detail)

- **SRS-UI-1**: The portal SHALL present a master list of Applications with per-application aggregate counts (assignment count, machine count) and, on selection, a detail view per machine that application runs on.
- **SRS-UI-2**: The portal SHALL present a master list of Machines with per-machine aggregate counts (application count, assignment count), online/offline status, and, on selection, a detail view of every application assigned to that machine.
- **SRS-UI-3**: Every Start/Stop control that would send an imperative command to a machine with no recent report SHALL be disabled, with an explanation, rather than silently accepting a click that cannot reach anything.

---

## 4. Non-Functional Requirements

| ID | Requirement |
|---|---|
| NFR-1 | **Outbound-only agents.** No managed machine SHALL require an inbound firewall rule for enList traffic. |
| NFR-2 | **Idempotent redeploy.** Re-running `enlist-deploy` with unchanged bytes and the same target SHALL be safe to repeat and SHALL not re-upload or duplicate an assignment. |
| NFR-3 | **Bounded staleness signal.** The gap between an agent going truly offline and the portal showing it as such SHALL be bounded by the heartbeat interval plus the staleness threshold (≤ ~7 minutes with current defaults), not unbounded. |
| NFR-4 | **Graceful shutdown.** An agent SHALL attempt to stop every running application within a configured grace period before the process is force-killed, on both a normal service stop and a deploy-driven application stop. |
| NFR-5 | **Crash isolation.** A plugin's own failure (a thrown exception during discovery, start, stop, or execute) SHALL be contained to that plugin/application and reported as a fault, not crash the runner process outright where avoidable, and never crash the agent process. |
| NFR-6 | **No secret data in plugin settings.** Plugin settings are a flat string dictionary read from a local file; the system makes no confidentiality guarantee about this file's contents (see [`BRD.md` §5.2](BRD.md#52-out-of-scope-not-present-in-the-current-implementation)). |

---

## 5. Traceability

Functional requirements above map to test coverage as follows (see [`Test-Plan.md`](../05-operations/Test-Plan.md) for the full matrix):

| Area | Primary test project |
|---|---|
| Agent registry, package lifecycle, isolation contract | `Enlist.ControlPlane.Tests` |
| Reconciliation, crash/restart, scheduling, package cache, log sink, startup failure | `Enlist.Agent.Tests` |
| Runner lifecycle (discovery, start/stop, pipe protocol) | `Enlist.Runner.Tests` |
| Deploy CLI packaging/upload/assign flow | `Enlist.Deploy.Tests` |
| Portal-side resolution (tag matching, conflict and port-collision prediction) | `Enlist.Portal.Tests` |
