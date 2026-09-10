# enList v3 — design

A from-scratch design for enList: a machine agent on every host, applications placed by the user and
reconciled from deployment manifests, services and jobs that bind to **nothing but the base class
library**, and a management portal separated from the runtime.

Informed by two sources: enList v2 as it stands today, and the Gridgistics Aware codebase
(see `Aware-Architecture-Notes.md`), which solved a comparable problem in 2009 and got several
things right that are worth taking.

---

## 1. The constraint that decides the architecture

> Start and stop services and jobs with no assembly binding other than `System`.

This is not a preference. It determines the process topology, because it forbids the component that
loads plugin code from having a dependency closure of its own.

Today, `AppInstance.exe` references Quartz, NLog, ASP.NET Core and `Microsoft.Extensions.*`. All of
it loads into the process before any plugin appears, and every plugin then has to agree with those
versions. That is the source of both collisions this codebase has actually hit — Quartz's
`System.Configuration.ConfigurationManager` 6.0.1 against SqlClient's 9.0.13, and `AppShared` baking
an NLog version into every plugin folder.

`AssemblyLoadContext` mitigates it. Removing the closure eliminates it. Aware demonstrated the
latter: its native host put exactly one managed assembly into the process — a ~250-line shim with no
dependencies beyond `System` — so there was nothing for a hosted application to conflict *with*.

**The rule that follows:** the component that loads plugin assemblies has zero `PackageReference`s,
enforced by a build check, permanently.

Everything below is a consequence of taking that seriously.

---

## 2. Topology

```
┌──────────────────────────────────────────────────────────────┐
│  Control plane            ASP.NET Core + SQL Server          │
│  · desired state (assignments)   · package store             │
│  · agent connections             · reported actual state     │
└────────────▲─────────────────────────────────────────────────┘
             │  outbound from agent (SignalR)
┌────────────┴─────────────────────────────────────────────────┐
│  enlist-agent.exe          Windows Service, one per machine  │
│  · reconciles desired vs actual   · owns the SCHEDULER       │
│  · supervises runners             · collects logs + health   │
│  · normal .NET app — MAY have dependencies                   │
└────────────┬─────────────────────────────────────────────────┘
             │  named pipe (System.IO.Pipes)
┌────────────┴─────────────────────────────────────────────────┐
│  enlist-runner.exe         One per running application       │
│  · ZERO package references — hard rule                       │
│  · PluginLoadContext → the application's assemblies          │
└──────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────┐
│  Portal                    MudBlazor → control plane API only│
└──────────────────────────────────────────────────────────────┘
```

Three tiers rather than two, and the middle one is what makes the zero-binding rule achievable.

### Why the scheduler lives in the agent

The obvious placement is the runner, next to the jobs. That would force the runner to parse cron
expressions, which means either a package reference (violating the rule) or hand-writing a cron
engine and owning its timezone and DST bugs forever.

Instead the **agent** computes fire times — it is a normal application and may reference Quartz,
Cronos or anything else — and sends the runner a `RunJob(jobId, runId)` message over the pipe when a
trigger fires. The runner only ever invokes what it is told to invoke.

Three consequences worth stating:

- The runner needs no notion of time or schedule at all.
- **Stopping a job's schedule becomes trivial and requires nothing of the plugin.** It means the
  agent stops firing its trigger. There is no plugin-side contract to implement, and it works for
  every job ever written. Stopping a run *already executing* is the one part a plugin must
  cooperate with — see "Stopping a job" below.
- Schedule changes take effect without restarting the application.

### Why the runner talks over a named pipe

HTTP in the runner would mean Kestrel, which means ASP.NET Core in the closure. `System.IO.Pipes` is
BCL. The pipe carries a small framed protocol: lifecycle commands down, state changes, log lines and
job results up.

---

## 3. The contract: attributes, matched by name

No shared contract assembly. No `AppShared`. No `AppSharedLegacy`.

```csharp
[EnlistService(Name = "Order Sync", Description = "Pulls orders from the upstream feed")]
public class OrderSync
{
    [EnlistStart] public void Start(CancellationToken stopping) { }
    [EnlistStop]  public void Stop() { }
}

[EnlistJob(Name = "Nightly Roll-up", Cron = "0 0 2 * * ?")]
public class NightlyRollup
{
    [EnlistExecute]
    public void Run(CancellationToken cancelling, IDictionary<string, string> settings) { }
}
```

### Matching is by name, never by type identity

The scanner already does this for interfaces, and for the right reason: types loaded into a
`MetadataLoadContext` are never reference-equal to compiled-against types, so `IsAssignableFrom`
silently returns false for every plugin. The same comparison — simple name — is used at runtime.

This is what removes the shared assembly. The plugin defines its own copy of the attribute types.
Ship them as a **source-only NuGet package** (a `.props` with a `Compile` item and no assembly), or
let people paste a single file. Either way nothing binary is shared. It is the same technique source
generators use for marker attributes.

### Injectable parameters are BCL-only

| Parameter | Purpose |
|---|---|
| `CancellationToken` | cooperative stop / cancel |
| `IDictionary<string,string>` | the application's settings |
| `Action<string>` or `TextWriter` | logging |

All live in the shared framework, so they are shared for free and cost nothing in the shared list.
There is deliberately no `ILogger<T>` and no `IConfiguration`: those are the three
`Microsoft.Extensions.*.Abstractions` assemblies currently pinned into `PluginLoadContext`'s shared
list, and dropping them takes that list to **empty**.

### Stopping a job

Two halves, and stopping a job in the portal does both.

1. **Scheduler-level** (no plugin involvement) — the agent stops firing the trigger, so the job does
   not run again.
2. **Run-level** — the runner cancels the `CancellationToken` handed to the run currently executing.

The second is the only place a plugin is involved, and only by checking a token it already receives.
The runner has no safe way to halt a thread it does not own, so a job that never checks its token
cannot be stopped — not by a command, and not by shutdown.

There is deliberately **no** suspend-and-continue. A paused run holds its connections, handles and
locks for as long as it is held, and the operator's question is nearly always "stop this", not "hold
this". Widening the required contract for a case most jobs do not have is exactly what this design
set out not to do.

### Stopping, and the escalation path

`[EnlistStop]` is called with a timeout. If it does not return, the agent kills the runner process.

This matters more than it looks. Aware needed a native CLR host and
`SetTimeoutAndAction(OPR_AppDomainUnload, 20s, eRudeUnloadAppDomain)` to guarantee it could evict
misbehaving code. On .NET there is no AppDomain and no such policy — but process-per-application
gives the same guarantee for free, and more reliably. **The one capability that justified Aware's C++
host is obtained here by process topology alone.**

### One contract across both runtimes

Because matching is by name and the parameter types are BCL, a **net472 plugin and a net10 plugin use
the identical contract with zero shared assembly**. The legacy runner and the modern runner look for
the same attribute names.

`AppShared`/`AppSharedLegacy` duplication — currently a permanent tax and a recurring source of
version-pinning bugs — disappears entirely.

### The trade being made

A missing `[EnlistStop]` becomes a publish-time rejection instead of a compile error. That is
acceptable *because* publish-time validation already exists and has a human standing in front of it,
but it is a real loss. Mitigate with a Roslyn analyzer shipped alongside the attributes; it takes
most of the sting out and costs little.

Second failure mode to design against: a typo in the attribute namespace means silent
non-discovery. The scanner should match on the **simple** name and warn when the namespace is
unexpected — `found Foo.EnlistServiceAttribute, expected Enlist.EnlistServiceAttribute` is a far
better message than an empty Services list.

---

## 4. Manifests and packaging

### Declare, do not carry

Aware's `Manifest` embedded the file bytes (`ImageBytes`, per-file content in `ManifestFileEntry`,
with `RemoveFileDataExcept(...)` to strip them when a light descriptor was wanted). Correct for
pushing over WCF in 2009; wrong now.

Split the two concerns:

**Manifest** — pure declaration, generated by the metadata scanner at publish time:

```
SchemaVersion, PackageDigest, TargetFramework
Services[]  { TypeName, AssemblyFile, Name, Description }
Jobs[]      { TypeName, AssemblyFile, Name, Description, Cron }
Assemblies[]{ Name, Version, File }          // for conflict reporting
Warnings[]
```

**Package** — the bits, content-addressed by SHA-256, stored once, fetched by digest.

Content addressing buys idempotent redeploy, dedup across machines, resumable transfer, cache
validation, and rollback expressed as "point the assignment at the previous digest." OCI thinking
without needing containers.

### The scanner gets better under the attribute model

Names, descriptions and cron expressions are now **in metadata**, readable by a
`MetadataLoadContext` scan without constructing anything. This is the limitation documented in the
current `ManifestEnricher`:

> `IAppService.Name` … are INSTANCE PROPERTIES, not attributes … A metadata scan can prove a type
> implements the contract; it cannot ask it what it is called.

Consequence: **the entire enrichment subsystem is deleted, not ported.** `ManifestEnricher`,
`MergeEnrichment`, `TryEnrichManifest`, the enrich-both-sides fix — all unnecessary. A never-run
application shows its real names immediately, and the class of bug that produced them (a tab had to
be visited before a name could be learned) cannot occur.

---

## 5. Desired state, not commands

The largest departure from enList v2, and what makes user-chosen placement actually work.

```
Assignment {
    ApplicationId
    PackageDigest
    Placement      : explicit machine | label selector
    DesiredState   : Running | Stopped | Paused
    Replicas
}
```

The control plane stores **intent**. Agents subscribe, diff intent against what they observe locally,
converge, and report actual state back.

Today the portal calls a REST endpoint on a remote host and hopes. Under reconciliation:

- an agent that reboots re-converges by itself
- a network partition self-heals when it ends
- no command can be lost, because there are no commands
- "where is this running?" is a query over reported state, not an inference
- rollout and rollback are edits to desired state

### Placement by label, not by role

Aware used a `ServerRole` flags enum (`All | SDK | Core | ServiceHost`) and gated startup on it. The
idea is right; the encoding is too rigid — every new category needs a code change.

Use labels: `role=batch`, `env=prod`, `region=east`, `gpu=true`. Selectors match against them.
Explicit machine pinning stays available, because sometimes the answer really is "that box."

---

## 6. Connection direction: agents dial out

Agents make **outbound** connections to the control plane. No inbound firewall rules on application
servers; works through NAT and DMZ boundaries.

enList v2 does the opposite — the portal reaches *into* each remote `AppHost`, which requires inbound
reachability to every machine that runs anything.

**Transport: SignalR.** Already used in IronCanary, so the team knows its failure modes, and it gives
agent presence for free — liveness falls out of connection state rather than needing a separate
heartbeat mechanism.

The heartbeat payload carries running application state, resource usage, recent events and log tail.
That is the monitoring layer, obtained without building Aware's separate `ICentralMonitoringService`
/ `ILocalMonitoringService` pair.

---

## 7. Logging without a logging framework

The runner cannot reference a logging framework. So:

- Plugin logs via the injected `Action<string>` / `TextWriter`, or plain `Console.WriteLine`.
- The runner captures stdout/stderr and the callback, frames each line, ships it to the agent over
  the pipe.
- The **agent** persists to disk and forwards to the control plane — and the agent may use whatever
  logging library it likes, because nothing loads plugin code into the agent.

If a plugin wants Serilog internally, that is entirely its business; it lives in the plugin's own
load context and the runner never sees it.

This also retires the `ENLIST_LOG_PATH` / `nlog.config` arrangement: log routing becomes the agent's
job, and the portal reads logs from the control plane rather than from a file share.

---

## 8. UI separation

```
Portal (MudBlazor)  →  Control Plane API  →  agents
```

The portal never talks to an agent. Aware separated its EnterpriseManager console from the runtime
for the same reason.

Two things this removes outright:

- **`IAppHostClient` and the local/remote duality.** There is one API. `LocalAppHostClient` versus
  `RemoteAppHostClient`, and every method existing twice, goes away.
- **The per-tick client churn.** `BuildServerClients()` currently hands out fresh client instances on
  every refresh tick, which is what made a `ReferenceEquals(Client, …)` guard unusable in the tab
  components. With one API and stable identity, that whole hazard evaporates.

Keep the existing tab components. They are fine; they just point somewhere simpler.

---

## 9. Carried over unchanged

| From v2 | Status |
|---|---|
| `AppScanner` / `MetadataLoadContext` scanning | Keep — improves under attributes |
| `PluginLoadContext` | Keep — shared list becomes **empty** |
| Startup assertion that isolation is engaged | Keep — it caught a real bug on its first run |
| `AssemblyConflictChecker` | Keep — moves to publish time |
| Blazor tab components | Keep — repointed at one API |
| `enlist.manifest.json` concept | Keep — split from the package |

---

## 10. Storage

SQL Server for the control plane:

```
Machines        agent identity, labels, last seen, version
Applications    logical app identity
Packages        digest, size, uploaded-by, uploaded-at
Manifests       parsed manifest per package digest
Assignments     desired state (the source of truth for placement)
AgentReports    actual state, resource usage, timestamps
JobRuns         run id, job, machine, start, end, outcome, output
Events          lifecycle and monitoring events
```

Package blobs on the filesystem or blob storage, keyed by digest.

---

## 11. Sequencing

Do not attempt a big-bang rewrite. Each step below is independently useful and independently
shippable.

**1. Runner.** Zero package references, attribute contract, named-pipe protocol, `PluginLoadContext`
with an empty shared list. Testable standalone with a stub driver on the other end of the pipe. This
step alone delivers what the current `PluginLoadContext` work is reaching for, and it is where the
zero-binding requirement is actually met.

**2. Agent, local only.** Supervises runners, owns the scheduler, collects logs. Replaces `AppHost`'s
process management. Still no control plane — reads assignments from a local file. At this point the
system is a strictly better single-machine enList.

**3. Control plane and desired state.** Agent dials out, reconciliation loop, assignments in SQL.
Multi-machine becomes real.

**4. Portal against the API.** Repoint the existing tabs; delete `IAppHostClient`.

**5. Legacy runner.** net472, same attribute contract, no shared assembly. Last because it is the
part being retired, and because the attribute model is what finally makes it cheap.

---

## 12. Open questions worth settling before starting

**Is desired-state reconciliation warranted?** It is real machinery. Above three or four machines it
pays for itself several times over. On a single server it is ceremony, and imperative
start/stop would be honest. Decide which world this is actually for, because the answer changes step 3
substantially.

**How much validation replaces the compiler?** A Roslyn analyzer shipped with the attributes is the
strongest answer, but it is a separate deliverable. The publish-time check is the floor.

**What is the story for a plugin that wants ASP.NET Core?** `IronCanary.Web` stands up a whole web
host inside `Start()`. That works — it is in the plugin's own load context — but it means the runner
must not assume plugin work is short-lived, and health reporting has to cope with a plugin that owns
a listening socket. Worth designing explicitly rather than discovering.

**Do we need in-flight job pause at all?** *Answered: no.* `[EnlistPause]`/`[EnlistResume]` was
built and then removed. Nothing in the portal or control plane ever sent it — the per-job control
there is Enable/Disable, which is the scheduler-level stop — and the capability actually missing was
cancelling a run in flight, which the design had assumed existed but nothing implemented: the token
every `[EnlistExecute]` receives was never cancelled by anything. `CancelJobCommand` closed that gap;
the pause contract went away with it.

---

## Appendix — what was taken from Aware, and what was not

**Taken**

- A host with no managed closure eliminates host-versus-plugin conflicts outright. The core benefit
  needs no C++; it needs discipline about dependencies.
- Attributes rather than a shared contract interface — Aware's `[ManagedService]`,
  `[PublishOnTcp]`, `[TaskPolicy]`, `[TaskExecutionProfile]` are the same idea.
- Manifest-driven deployment as a first-class concept, with a declared hosting model.
- Role/label gating so the same package can be deployed widely and started selectively.
- Separating the management console from the runtime.
- Filterable runtime tracing that can be enabled for a subset of traffic in production.

**Not taken**

- `ICLRPolicyManager` and the native reliability layer — deprecated in CLR 4, absent in .NET Core+,
  and its one essential guarantee (evicting misbehaving code) is delivered here by killing a process.
- AppDomains. `AssemblyLoadContext` plus process isolation replaces them.
- Manifests carrying file bytes — replaced by content-addressed packages.
- WCF, Windows Workflow Foundation, MSMQ, the GAC.
