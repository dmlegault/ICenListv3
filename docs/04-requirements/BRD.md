# Business Requirements Document (BRD)

**Product:** enList v3 ("iron canary")
**Document status:** Derived from the current implementation as of 2026-09-03. No separate prior BRD exists; this document was reverse-authored from the shipped system (source under `src/`, `samples/`, `contracts/`) to give the project a durable requirements record. It is a companion to, not a replacement for, the original **[enList-v3-Design.md](../03-architecture/enList-v3-Design.md)** — the from-scratch technical design this whole rewrite was built against, informed by both enList v2 and a 2009-era prior-art system, "Aware" (see [Aware-Architecture-Notes.md](../06-background/Aware-Architecture-Notes.md)). Where this BRD's framing of business intent and the design doc's own stated constraints differ, the design doc is authoritative on *why* a technical choice was made; this BRD is authoritative on the business objective that choice serves.

---

## 1. Purpose

enList is an internal platform for deploying and supervising long-running Windows applications ("services") and scheduled jobs across a fleet of machines, from one central place, without an operator ever RDP-ing into a box to copy files or babysit a process. enList v3 is a ground-up rewrite of an existing internal tool ("enList v2"), explicitly informed by two sources named in the design doc's own opening: enList v2 itself (the baseline for behavior deliberately kept — v2's header branding, v2's tolerant plugin-discovery posture — and behavior deliberately replaced, chiefly v2's shared-assembly plugin contract, wherever that caused version collisions) and the Gridgistics "Aware" codebase (c. 2009), which the design doc credits with having solved a comparable zero-dependency plugin-hosting problem first. The "iron canary" branding itself is not a name invented for this rewrite — the design doc notes SignalR was chosen for the agent-to-control-plane transport partly because it was "already used in IronCanary, so the team knows its failure modes," identifying IronCanary as an existing, related internal system this team already operates.

## 2. Business Problem

Before enList, deploying an internal service or scheduled job to a fleet of machines meant:

- An operator manually copying build output to each target machine (RDP + file copy, or a hand-rolled script).
- No single place to see what is actually running, on which machines, in what state.
- No safe way to roll an application back to a previous build short of re-copying old files, if they were even kept.
- No standard way to stop one misbehaving service inside a multi-service application without bouncing the whole application.
- No audit trail connecting "what's running right now" to "what we told it to run."

This is slow, error-prone (a missed machine, a stale copy), and has no record of intent — nothing distinguishes "this machine is supposed to be running v3" from "this machine happens to still have v2's files on disk."

## 3. Business Objectives

| # | Objective | How enList v3 addresses it |
|---|---|---|
| 1 | Deploy an application to any number of machines from one action | `enlist-deploy` uploads the build once (content-addressed, deduplicated); a single tag-selector policy rule then places it on every matching machine at once. The two are deliberately separate acts, so a redeploy cannot silently change WHERE something runs. |
| 2 | Declare intent, not just push files | Every machine's desired state (`Running`/`Stopped`, which package/path, cron overrides) is a row in a central database (`ApplicationPolicyEntity`), not inferred from whatever happens to be on disk. |
| 3 | See fleet-wide status from one screen | Enlist.Portal's Applications and Agents views show live state per application, per machine, refreshed continuously. |
| 4 | Target machines by role, not by name, as the fleet grows | tag-selector placement (`role=web`, `env=prod`, etc.) — a newly-registered machine matching an existing selector picks up its policies automatically, with no redeploy action needed. |
| 5 | Roll back quickly when a build is bad | Packages are content-addressed and versioned per application (`PackageEntity.VersionNumber`); redeploying an older digest is the same operation as deploying a new one. |
| 6 | Control one moving part without disturbing the rest | Imperative per-service/per-job Start/Stop commands, independent of the declarative "is this whole application supposed to be running" state. |
| 7 | Minimize the network/security footprint on managed machines | Agents dial **out** to the control plane only; no inbound port is ever opened on a managed machine for enList's own purposes. |
| 8 | Let a plugin author ship without depending on a specific enList assembly version | The plugin attribute contract (`contracts/EnlistAttributes.cs`) is distributed as source, matched by attribute **name** at discovery time, not by compiled type identity — the exact version-collision failure mode v2 was documented as having. |

## 4. Stakeholders

| Stakeholder | Interest |
|---|---|
| Operators / on-call engineers | Deploy, start/stop, and observe applications across the fleet from the portal without touching individual machines. |
| Application developers ("plugin authors") | Write a Service or Job class against the `Enlist` attribute contract and have it discovered, run, and supervised without writing any deployment or process-management code themselves. |
| Fleet/infrastructure owners | Need every managed machine's outbound-only network posture preserved, and a registry of what's installed where. |
| Auditors / incident responders | Need a durable record of desired state changes (`ApplicationPolicyEntity.UpdatedAtUtc`) and recent application log output per machine. |

## 5. Scope

### 5.1 In scope (current implementation)

- Central control plane: agent registry, application-policy (desired-state) storage, package storage and versioning, status-report storage, log storage — all in one SQL Server database.
- Agent process per managed machine: reconciles desired state, supervises child processes, restarts on crash with backoff, forwards status and logs, accepts imperative commands.
- Runner process per running application instance: discovers and hosts the application's declared services and jobs inside an isolated `AssemblyLoadContext`.
- Web portal: fleet-wide visibility and control (start/stop at the application, service, and job level; tag management; package management; log tailing).
- CLI deploy tool: package and upload in one action. It does not assign — placement is declared separately, as a policy rule.
- One placement model: tag selectors, with an implicit per-agent self-tag so "just this agent" needs no separate mechanism.
- Two package source models: a pre-existing local path on the machine, or a content-addressed package the agent downloads and caches.

### 5.2 Out of scope (not present in the current implementation)

- Multi-tenancy / role-based access control in the portal (the control plane authenticates callers as of 2026-09-11 — [Authentication-Design.md](../03-architecture/Authentication-Design.md) — but the portal does not yet authenticate people; roles exist only as `Operator` and `Viewer` on API keys).
- Cross-platform agents (the agent's Windows Service hosting and Job Object process-tree cleanup are Windows-specific; Job Object usage is explicitly guarded as Windows-only and optional even there).
- Blue/green or canary rollout orchestration — pointing a rule at a new digest applies to every matching machine as soon as each reconciles; there is no phased rollout primitive.
- Secrets management — application settings are a flat `IDictionary<string,string>` read from a colocated `*.settings.json` file; there is no encrypted-secret or vault integration.
- Metrics/alerting pipeline — the system reports application-level state and pid/restart-count only; there is no integration with an external monitoring or paging system.

## 6. Success Criteria

- An application can be placed on N machines by a single tag-selector rule, off one `enlist-deploy` upload, with zero manual file copying.
- The portal reflects a machine's real running state (per-service, per-job) within one polling interval (a few seconds) of a change taking effect on that machine.
- A machine whose agent goes offline is visibly distinguished ("Offline"/"Stale") from one that is online but has nothing new to report, within the 5-minute staleness threshold the portal applies.
- Deleting a package or a machine that is still in active use is rejected by the API, not silently allowed.
- No enList component requires an inbound network rule on a managed machine.

## 7. Constraints and Assumptions

- Windows-only managed machines (agent hosting, Job Object supervision, and the reference plugin samples all assume Windows).
- .NET 10 runtime available on every managed machine, the control plane host, and the portal host.
- SQL Server (or SQL Server-compatible, e.g. LocalDB for development) reachable from the control plane.
- Plugin authors follow the `contracts/EnlistAttributes.cs` source-inclusion convention rather than referencing a compiled assembly.
- The control plane has an authentication layer (bearer tokens; `Off` is permitted only on loopback, with no override). Agents enroll with a join token and present their own credential. Until the portal authenticates people (step 3 of [Authentication-Design.md](../03-architecture/Authentication-Design.md)), a deployment with a portal runs the control plane `Off`, which forces it onto loopback — the trusted boundary is the machine, not the network.

## 8. Related Documents

See [`docs/README.md`](../README.md) for the full documentation set: SRS, FRS, SAD, HLD, LLD, Database Design, API Specification, UI/UX Design Specification, Developer Setup Guide, Test Plan, Deployment & IaC, and Runbook.
