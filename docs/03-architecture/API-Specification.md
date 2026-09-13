# API Specification

**Product:** enList v3 — Enlist.ControlPlane
**Base URL (development):** `http://localhost:5293`
**Format:** JSON over HTTP (minimal APIs, `System.Text.Json`, camelCase property names on the wire — the .NET `HttpClient` JSON helpers' `Web` defaults). Authentication is a bearer token per request; §0 has the modes, the token kinds and which endpoint wants which.
**Document status:** Derived from `src/Enlist.ControlPlane/Program.cs` and `src/Enlist.ControlPlane.Contracts/`. Originally 2026-09-03; **every route, query parameter, request shape and status code re-verified against the source on 2026-09-08**, which corrected several that the terminology rename had left behind (`/labels` was actually `/tags`, `?machineName=` was `?agentName=`, the hub path was `/hubs/application-policies` not `/hubs/assignments`, an `AgentName` field that no longer exists, and a 409 on agent delete that is no longer returned) and added three endpoints that were missing entirely.

---

## 0. Authentication

**Mode.** `Authentication:Mode` is `Required` (the default when the section is absent) or `Off`. Two rules are enforced at startup and refuse to start otherwise: `Off` is honoured only when every listener is bound to loopback (`localhost`, `127.0.0.1`, `::1`) — there is no override — and under `Required` no listener off loopback may be plain `http://`. `GET /health` reports the mode in `authentication`, so a caller can tell whether a credential is needed before failing.

**Credentials.** One scheme: `Authorization: Bearer <token>`. Three token kinds, told apart by prefix: `enla_` agent credentials (one per agent, bound to its name), `enlk_` management API keys (with a role, `Operator` or `Viewer`), and `enlj_` join tokens (good for enrollment and nothing else). On the hub path the token may instead be the `access_token` query parameter, which is how the SignalR client sends it. Only SHA-256 hashes are stored; every secret is shown once.

**Policies.** Three, in one table (`Authentication/EndpointPolicies.cs`, the code twin of [Authentication-Design.md §7](Authentication-Design.md)): agents act as themselves (the `{name}` in the route must equal the name in the credential), reads are `Viewer`, writes are `Operator`. An endpoint missing from the table is refused to everyone — fail closed. **401** is no credential or an unknown, revoked, expired or used-up one (with `WWW-Authenticate: Bearer`); **403** is a valid credential of the wrong kind, name or role.

| Policy | Endpoints |
|---|---|
| Anonymous | `GET /health` |
| Join token | `POST /api/agents/enroll` |
| Agent (own name) | `GET /api/agents/{agentName}/policies`, `POST .../report`, `POST .../logs`, `PUT /api/agents/{name}/capabilities`, the hub |
| Agent or Viewer | `GET /api/packages/{digest}` |
| Viewer | every other `GET` |
| Operator | every `POST`, `PUT` and `DELETE` not listed above — including all of `/api/api-keys` and `/api/join-tokens` (listing included) and `DELETE /api/agents/{name}/credential` |

### `POST /api/agents/enroll`
**Bearer:** a join token. **Body:** `EnrollAgentRequest { AgentName }`
**201** → `EnrollAgentResponse { AgentName, AgentToken }` — the token is returned once and consumes one use of the join token. The agent row is created if it did not exist.
**400** the name is not a valid agent name. **401** the join token is unknown, expired, revoked or used up. **409** the name already holds a live credential — revoke it first (`revoke-agent`, or `DELETE /api/agents/{name}`, which cascades). Under `Off` (loopback only) enrollment works without a join token.

### Keys and join tokens (Operator)
`POST /api/api-keys` — body `CreateApiKeyRequest { Name, Role, ExpiresIn? }` (`Role` is `Operator` or `Viewer`; `ExpiresIn` like `30d`, `1h` or `never`, default 90 days) → **201** `CreateApiKeyResponse { Id, Name, Role, ExpiresAtUtc, Key }`, the key once. **400** a bad role or expiry; **409** a live key already has that name.
`GET /api/api-keys` → `ApiKeyDto[]` (`Status` is `live`, `expired` or `revoked`; never the key). `DELETE /api/api-keys/{name}` → **204**; **404** no live key of that name.
`POST /api/join-tokens` — body `CreateJoinTokenRequest { ExpiresIn?, Uses? }` (default 24 h and unlimited uses; `never` is refused) → **201** `CreateJoinTokenResponse { Id, ExpiresAtUtc, UsesRemaining, Token }`. `GET /api/join-tokens` → `JoinTokenDto[]` (`Status` adds `used up`). `DELETE /api/join-tokens/{id}` → **204** / **404**.
`DELETE /api/agents/{name}/credential` → **204** revokes the agent's credential and keeps its registry row (`DELETE /api/agents/{name}` does both); **404** it holds none. `AgentDto.CredentialState` is `none`, `live` or `revoked`.

**Who did it.** A management caller may add `X-Enlist-Operator: <DOMAIN\user>` naming the person behind the key — the portal does, with the Windows identity of whoever is looking at the page. The control plane never authorizes on it; it logs it. Every administrative write — anything but a `GET`, and not what an agent does as itself (reports, logs, capabilities) — leaves one line in the control plane's log: `Audit: PUT /api/agents/WEB-07/tags -> 200 by key 'portal' for CORP\alice`. An enrollment is audited as `by join token <id>`.

**Bootstrap, without HTTP.** Verbs on the control plane executable, run on its host against its database, because the first Operator key cannot be created by an Operator who does not exist yet:

```
Enlist.ControlPlane.exe create-api-key    --name portal --role Operator [--expires 90d|never]
Enlist.ControlPlane.exe create-join-token [--expires 24h] [--uses 50]
Enlist.ControlPlane.exe revoke-api-key    --name <name>
Enlist.ControlPlane.exe revoke-agent      --name <agent>
Enlist.ControlPlane.exe list-keys
Enlist.ControlPlane.exe list-join-tokens
```

**Status (2026-09-11):** all three steps of [Authentication-Design.md §13](Authentication-Design.md) are implemented — the control plane (`AuthenticationTests`, `AccessEndpointsTests`), the agent (`AgentCredentialTests`) and the portal and tools (`PortalAuthenticationTests`, `PortalRolesTests`, `ControlPlaneApiClientTests`; `--api-key` in `DeployCliTests`). Still deferred: `--control-plane-ca` on the agent (a trust anchor for a private CA; the machine store serves until then) and an audit *table* (the audit is the structured log line below). The demo runs `Off` on loopback by choice, not necessity.

---

## 1. Agents

### `GET /api/agents`
Returns every registered agent, ordered by name.
**200** → `AgentDto[]`

### `GET /api/agents/{name}`
**200** → `AgentDto`
**404** if not registered.

### `PUT /api/agents/{name}/tags`
Replaces (not merges) an agent's tag set — a merge would make "remove this tag" impossible to express. **Creates the agent row if it doesn't exist yet**, which is how an agent is pre-registered before its process has ever connected. Notifies that agent over SignalR, because a tag change can newly match or unmatch existing tag-selector policy rules.
**Body:** `SetAgentTagsRequest { Tags: { [key]: string } }`
**200** → `AgentDto`

> Every agent additionally carries an **implicit** `agent=<name>` tag that is never stored here. "Run on exactly this one agent" is therefore just the most specific possible tag selector, not a separate targeting mechanism — see [`LLD.md` §3](LLD.md#3-tag-selector-matching).

### `PUT /api/agents/{name}/scheduling`
Includes or excludes an agent from scheduling entirely. `false` stops everything it currently hosts and prevents it picking up any rule — **even one that already matches it** — until re-included. Pushes over SignalR so the agent reconciles immediately rather than at its next poll.
**Body:** `SetAgentSchedulingRequest { SchedulingEnabled: bool }`
**204** on success.
**404** if not registered — unlike the tags endpoint, this one does **not** create the row, because excluding an agent that has never existed has no meaning.

This needs no agent-side code at all: an excluded agent simply resolves to an empty policy list, and the agent's existing reconciliation already tears down everything absent from that list.

### `DELETE /api/agents/{name}`
**204** on success.
**404** if not registered.

No 409. Deletion is never blocked — the earlier "409 if an explicit placement still targets this agent by name" rule disappeared along with explicit targeting: a tag selector doesn't name an agent, so there is nothing to hold a reference. A rule whose selector still matches simply governs one fewer agent.

`AgentReports`/`AgentLogs` for the deleted name are deliberately **retained**, not cascade-deleted. There is no FK between them and `Agents`: they are history keyed by a name string rather than owned rows, and if that name registers again later (a rebuilt box reusing a hostname) the history is still worth having. Both remain subject to their retention sweeps (`LogRetention`, `ReportRetention` — the latter always keeps the newest report per name).

---

## 2. Application Policies

### `GET /api/application-policies?agentName={name}` (query param optional)
Without `agentName`: every policy rule in the system.
With `agentName`: every rule that currently **governs that agent** — each rule whose tag selector its current tags satisfy, including via the implicit `agent=<name>` tag. This is the same resolution the agent itself uses (see [`LLD.md` §3](LLD.md#3-tag-selector-matching)), so the portal and the agent cannot disagree about who matches what.
**200** → `ApplicationPolicyDto[]`

### `POST /api/application-policies`
**Body:** `CreateApplicationPolicyRequest { ApplicationName, Path?, DesiredState, CronOverrides?, TagSelector?, PackageDigest?, RuntimeFlavor?, Isolation? }`

Targeting is **tags only** — there is no `AgentName` field. Exactly one of `Path`/`PackageDigest` must be set.

**201** → `ApplicationPolicyDto`, `Location: /api/application-policies/{id}`

**400** with a message naming the offending value, for any of:

| Rule | Message shape |
|---|---|
| `DesiredState` not `Running`/`Stopped` | `DesiredState must be 'Running' or 'Stopped', got '…'` |
| Both or neither of `Path`/`PackageDigest` | `Specify exactly one of Path or PackageDigest, not both and not neither.` |
| `ApplicationName` not a valid name — letters, digits, `-`, `_`, `.`; starts with a letter or digit; at most 64; not a Windows device name (`ApplicationNames` in Contracts) | `'…' is not a valid application name: …` |
| `PackageDigest` not 64 hex characters | `'…' is not a package digest: …` |
| `PackageDigest` names a package this control plane does not have | `PackageDigest '…' is not a package this control plane has — upload it first …` |
| A `CronOverrides` value is not a cron expression — five fields or six, `?` for `*` (`CronExpressions` in Contracts) | `CronOverrides['job']: '…' has 4 field(s); …` / `CronOverrides['job']: '…' is not a valid cron expression: …` |
| `RuntimeFlavor` not in `RuntimeFlavors.All` | `RuntimeFlavor must be one of [...], got '…'` |
| `Isolation.Mode` not in `IsolationModes.All` | `Isolation.Mode must be one of [...], got '…'` |
| Flavor contradicts the package's detected flavor, or `net472` + `container` | see §7.1 |
| Port out of range, unknown protocol, or a static host-port collision | see §7.2 |

**No 409.** Duplicate-rule prevention went away with explicit targeting — two rules that overlap are detected at *resolution* and surfaced as a `ConflictReason`, not blocked at write time. Two selectors can begin overlapping later anyway (someone re-tags an agent), so a write-time check could never have been sufficient.

Notifies the affected agent(s) over SignalR on success.

### `PUT /api/application-policies/{id}`
**Body:** `UpdateApplicationPolicyRequest { Path?, DesiredState?, CronOverrides?, PackageDigest?, RuntimeFlavor?, Isolation? }` — every field optional; only supplied fields change. Setting `Path` clears `PackageDigest` and vice versa (they're alternatives, not independent).

`TagSelector` is **not** editable here — changing *which* agents qualify is a delete-and-recreate, not an update. `RuntimeFlavor` is deliberately different: a redeploy can legitimately migrate an application off (or onto) a legacy runtime, so it follows the same idempotent update path as `Path`/`PackageDigest`.
**200** → `ApplicationPolicyDto`
**404** if the id doesn't exist.
**400** on the same validation rules as `POST` — including a `PackageDigest` that is malformed or that names no stored package. A digest is stored lowercase however it was written.

### `DELETE /api/application-policies/{id}`
**204** on success. **404** if the id doesn't exist. Notifies the affected agent(s) over SignalR.

---

## 3. Packages

### `POST /api/packages?application={name}` (query param optional)
Body: raw zip bytes (request body stream). Digest is computed server-side from the received bytes.
**200** → `UploadPackageResponse { Digest, SizeBytes, AlreadyExisted }`
If `application` is supplied and the digest already existed with no application name yet recorded, this backfills it (first-write-wins — never overwrites an existing name).
**400** if `application` is not a valid application name (the same rule as `POST /api/application-policies`). **413** if the body exceeds `PackageStorage:MaxUploadBytes` (default 512 MB; the portal's own picker allows 500 MB) — this endpoint raises Kestrel's stock 30 MB limit, nothing else does. **400** if the body is not a zip archive — nothing is stored, neither a row nor a blob. `VersionNumber` is unique per application (a filtered unique index; two uploads that compute the same next number are renumbered, never both stored as one version).

### `GET /api/packages/{digest}`
Streams the zip bytes. **200** `application/zip`, **404** if the digest isn't stored, **400** if `{digest}` is not 64 hex characters — checked before anything becomes a path.

### `GET /api/packages`
**200** → `PackageInfo[] { Digest, SizeBytes, UploadedAtUtc, ApplicationName, VersionNumber, RuntimeFlavor }`, newest-uploaded first.

### `GET /api/packages/{digest}/manifest`
Returns what enList would actually discover inside — services, jobs, descriptions — without deploying it anywhere. Scanned once, at upload, and stored on the package row (`Packages.ManifestJson`); served from there on every request. A row from before the column existed, or whose scan failed at upload, is scanned on its first request and kept.
**200** → the scan result (`PackageManifestScanner.Scan`).
**404** if the digest isn't stored; **400** if it is not 64 hex characters.

This is what the portal's "what's in this package?" dialog reads. It answers the same question `enlist-runner --check` answers locally ([`Application-Developer-Guide.md` §3](../02-building-applications/Application-Developer-Guide.md)), from the other end: before a package is placed anywhere, rather than against a build output on a developer's disk.

### `DELETE /api/packages/{digest}`
**204** on success. **404** if not found; **400** if the digest is malformed. **409** if any policy rule (Running or Stopped) still references this digest — body names the blocking application(s). No force option.

---

## 4. Imperative Commands

### `POST /api/agents/{name}/commands`
**Body:** `AgentCommandRequest { ApplicationName, TargetKind: "Service"|"Job", TargetName, Action: "Start"|"Stop" }`
**204** on acceptance for delivery — **not** an acknowledgement that the target agent received or acted on it (fire-and-forget over SignalR; the agent's next status report is the only way to observe the effect).
**404** if the agent isn't registered.
**400** if `TargetKind`/`Action` aren't recognized values.

---

## 5. Agent-Facing Endpoints

### `PUT /api/agents/{name}/capabilities`

**Body:** `SetAgentCapabilitiesRequest { Capabilities: AgentCapabilitiesDto }` → **204**.

Reported BY the agent, not set by an operator: only the agent can know whether a container engine on its own host actually answers. The container-engine part is a PROBE result (`docker version` against the SERVER), re-run on every heartbeat — being started with `--container-image` proves a flag was passed, not that an engine exists, is running, or is reachable. Upserts the agent row, so a report from an agent the control plane has never seen still lands.

These are called by `Enlist.Agent`, not typically by the portal or a human operator.

### `GET /api/agents/{agentName}/policies`
Every rule currently governing this agent, resolved down to at most one outcome per application (see [`LLD.md` §3](LLD.md#3-tag-selector-matching)). Also upserts `LastSeenUtc` — this call doubles as the liveness signal an agent produces just by functioning normally.
**200** → `ApplicationPolicyDto[]`

### `POST /api/agents/{agentName}/report`
Body: raw JSON — the agent's `AgentStatusSnapshot`, stored as-is (opaque to the control plane; see [`Database-Design.md` §5](Database-Design.md#5-json-in-column-fields--rationale)). Also upserts `LastSeenUtc`.
**202 Accepted**

**`AgentStatusSnapshot` shape** (agent-defined, not control-plane-validated):

```jsonc
{
  "generatedAtUtc": "2026-09-03T14:44:07.39Z",
  "applications": [
    {
      "name": "OrderProcessor",
      "state": "Running",           // Starting | Running | Restarting | Failed | Stopped
      "pid": 173100,                 // null if not currently running
      "restartCount": 0,
      "description": "Processes incoming customer orders...",  // from [EnlistApplication], or null
      "services": [
        { "name": "Order Intake Listener", "state": "Running", "description": "Watches..." }
      ],
      "jobs": [
        {
          "name": "Abandoned Cart Sweep",
          "lastRunUtc": "2026-09-03T14:30:00Z",
          "lastRunOutcome": "Succeeded",   // or "Failed", "Cancelled"; null until it first runs
          "paused": false,
          "cron": "0 */30 * * * ?",
          "description": "Finds carts inactive for 24h..."
        }
      ]
    }
  ]
}
```

### `GET /api/agents/{agentName}/report/latest`
**200** → the most recent `SnapshotJson`, returned verbatim as `application/json`.
**404** if this agent has never reported.

### `POST /api/agents/{agentName}/logs`
**Body:** `SubmitLogEntriesRequest { Entries: LogEntrySubmission[] }` where each entry is `{ ApplicationName, Level, Source, Text, TimestampUtc }`.
**202 Accepted** (also for an empty batch — a no-op).

### `GET /api/agents/{agentName}/logs?application={name}&sinceId={id}&take={n}`
`sinceId` (optional, default 0): return only rows with `Id > sinceId`. `take` (optional, default 200, clamped to 1–1000).
**200** → `AgentLogEntryDto[] { Id, ApplicationName, Level, Source, Text, TimestampUtc }`, ordered by `Id` ascending.

---

## 5a. Endpoint Feed — for a reverse proxy

enList publishes WHERE applications are listening. It does not route, terminate TLS, or own hostnames — see [`Container-Story.md` §8.4](Container-Story.md) for why that boundary is deliberate.

### `GET /api/endpoints`

Optional `?application=NAME` filter.

**200** → `EndpointDto[]`, ordered by application then agent.

`Stale: true` means the reporting agent has not checked in within the 5-minute liveness window: the value is last-known rather than confirmed. Reported rather than dropped, because "the agent went quiet" and "the application stopped" are different events an operator must be able to distinguish.

This endpoint is the only thing in the control plane that parses `AgentReports.SnapshotJson`, which is otherwise stored opaquely so the agent's status shape can evolve without a server migration. Reading at query time is a far weaker coupling than storing parsed columns: an unreadable shape means that one agent contributes nothing, never that ingestion breaks.

### `GET /api/endpoints/traefik`

**200** → Traefik dynamic configuration (HTTP provider format).

Emits `http.services` and deliberately **no** `http.routers`. A service is the live server set, which is what enList uniquely knows; a router is a hostname, a certificate and a path rule, which is how an organisation wants traffic to arrive. Declare routers in your own Traefik config pointing at a service named here, and enList keeps the servers underneath current. Stale endpoints are EXCLUDED here — a proxy routing live traffic to a presumed-dead host is a different question from a human reading a screen.

```json
{"http":{"services":{"orderprocessor":{"loadBalancer":{"servers":[{"url":"http://HOST:50605"}]}}}}}
```

Verified against a real Traefik 3.1, including it following an application to a new ephemeral port after a container restart.

## 5b. Health — for probes, the installer and the portal

### `GET /health`
Liveness with a verdict. The ONE anonymous endpoint once authentication is Required — `EndpointPolicies` gives it `Anonymous` and everything else a credential — because a probe, a load balancer and the installer’s Verify step all have to reach it before anyone has a key. It says nothing secret: which product and build is answering, whether its database does, and whether authentication is on — never the connection string.
**200** → `HealthDto` with `Status: "Healthy"` — the database answered.
**503** → the same `HealthDto` with `Status: "Unhealthy"` and `Database.Error` set — the control plane is up but its database is not. Still a JSON body naming the product, so a caller can tell *enList, degraded* from *not enList at all* (a connection error, or a non-JSON body). Only ever seen after a successful start: outside Development the process refuses to start against an absent database ([`Deployment-IaC.md` §1.4](../05-operations/Deployment-IaC.md)).

The database check is bounded at 5 s, so a database that accepts the connection and never answers cannot turn a probe into a hung request. `Database.LatestMigration` is the newest applied migration id — how the installer's Database page and a deploy pipeline can tell whether the schema is current without a second call.

```jsonc
{
  "status": "Healthy",
  "product": "enList control plane",
  "version": "3.0.0",
  "database": { "reachable": true, "latestMigration": "20260911192322_InitialCreate", "error": null },
  "checkedAtUtc": "2026-09-11T14:02:11.4Z",
  "authentication": "Required"
}
```

Point service monitors, load balancers and the installer's *Verify* at this, not at `/api/agents`.

---

## 6. Real-Time Push — `ApplicationPolicyHub` (SignalR)

**Hub path:** `/hubs/application-policies` (`ApplicationPolicyHubContract.HubPath` — the one place it is defined, referenced by both the hub registration and the agent's client)

| Direction | Method | Payload | Purpose |
|---|---|---|---|
| Client → Hub | `JoinAgentGroup(agentName)` | — | Subscribes this connection to `agent:{agentName}`'s group. **Must be re-invoked after every reconnect** — SignalR group membership is scoped to the connection ID, which changes on reconnect (see [`LLD.md` §8](LLD.md), [`Runbook.md`](../05-operations/Runbook.md)). |
| Hub → Client | `ApplicationPoliciesChanged` | none | "Something about your policies changed — re-fetch via `GET /api/agents/{name}/policies`." Carries no payload deliberately, so the REST contract stays the single source of truth for what actually changed. |
| Hub → Client | `ExecuteCommand` | `AgentCommandRequest` | An imperative command pushed via `POST /api/agents/{name}/commands`. |

Both push types are **fire-and-forget** from the server's point of view — `Clients.Group(...).SendAsync(...)` does not error if the group is empty, and the server does not track delivery or acknowledgement.

---

## 7. Data Transfer Objects

### `HealthDto`, `HealthDatabaseDto`
```csharp
record HealthDto(string Status, string Product, string Version, HealthDatabaseDto Database, DateTimeOffset CheckedAtUtc, string Authentication);
record HealthDatabaseDto(bool Reachable, string? LatestMigration, string? Error);
```
`Status` is `Healthy` or `Unhealthy` and mirrors the HTTP status (200 / 503); `Error` is set only when `Reachable` is false. `Authentication` is `"Required"` or `"Off"`, so an agent with no credential — or the installer’s Verify — can tell whether one is needed before failing on a 401. See §5b.

**File:** `src/Enlist.ControlPlane.Contracts/`

```csharp
// Parameter order here is APPEND-ONLY. Several call sites construct these positionally, so inserting
// a parameter would silently misassign values rather than fail to compile.
record ApplicationPolicyDto(
    Guid Id, string ApplicationName, string? Path, string DesiredState,
    IReadOnlyDictionary<string,string> CronOverrides, DateTimeOffset UpdatedAtUtc,
    IReadOnlyDictionary<string,string> TagSelector,
    string? PackageDigest = null, string RuntimeFlavor = RuntimeFlavors.Default,
    string? ConflictReason = null, IsolationSpec? Isolation = null);

record AgentDto(string Name, IReadOnlyDictionary<string,string> Tags,
    DateTimeOffset FirstSeenUtc, DateTimeOffset LastSeenUtc,
    bool SchedulingEnabled = true, AgentCapabilitiesDto? Capabilities = null,
    // One of AgentCredentialStates: does this agent hold a live credential, was it revoked, or has
    // it never enrolled - which under authentication Off is every agent.
    string CredentialState = AgentCredentialStates.None);

// HOW an application is hosted — orthogonal to RuntimeFlavor, which is WHAT it needs to run at all.
record IsolationSpec(string Mode = "process", string? Image = null,
    IReadOnlyList<PortMapping>? Ports = null, IReadOnlyList<string>? Networks = null,
    IReadOnlyDictionary<string,string>? Env = null);

record PortMapping(int ContainerPort, int? HostPort = null, string Protocol = "tcp", string? Name = null);

// What an agent can actually do — the container engine part is a PROBE result, not a reflection of
// configuration, so the portal cannot claim a capability the agent does not have.
record AgentCapabilitiesDto(IReadOnlyList<string> IsolationModes, ContainerEngineStatus? ContainerEngine = null, string? PushChannel = null);

record ContainerEngineStatus(bool Available, string? Version, string? Error, string? Image, DateTimeOffset CheckedAtUtc, string? Engine = null);
// Engine: "docker" | "wslc" — which engine the agent drives; null from an agent older than the field.
// PushChannel: "connected" | "reconnecting" | "disconnected" (PushChannelStates) — the agent's SignalR connection, reported so an Online agent that cannot receive a push is distinguishable from one that can.

// One place an application is listening, fleet-wide. No hostname, no TLS, no routing — see
// Container-Story.md §8.4 for why enList reports endpoints and is deliberately not a router.
record EndpointDto(string ApplicationName, string AgentName, string? Name, string Protocol,
    int ContainerPort, int HostPort, string HostAddress, bool Stale, DateTimeOffset ReportedAtUtc);

record AgentCommandRequest(string ApplicationName, string TargetKind, string TargetName, string Action);
    // TargetKind: "Service" | "Job"   Action: "Start" | "Stop"

record PackageInfo(string Digest, long SizeBytes, DateTimeOffset UploadedAtUtc,
    string? ApplicationName = null, int? VersionNumber = null,
    string RuntimeFlavor = RuntimeFlavors.Default);

record UploadPackageResponse(string Digest, long SizeBytes, bool AlreadyExisted,
    string RuntimeFlavor = RuntimeFlavors.Default);

record AgentLogEntryDto(long Id, string ApplicationName, string Level, string Source, string Text, DateTimeOffset TimestampUtc);
record SubmitLogEntriesRequest(IReadOnlyList<LogEntrySubmission> Entries);
record LogEntrySubmission(string ApplicationName, string Level, string Source, string Text, DateTimeOffset TimestampUtc);
```

> **Note on parameter order:** `ApplicationPolicyDto`'s constructor parameter order is append-only by convention across this codebase — several call sites construct it positionally, and reordering (as opposed to appending a new trailing optional parameter) would silently misassign values rather than fail to compile.

### 7.1 RuntimeFlavor

```csharp
static class RuntimeFlavors
{
    const string Default = "net10.0";
    const string NetFramework472 = "net472";
    static readonly IReadOnlyList<string> All = [Default, NetFramework472];
}
```

Declares which `enlist-runner` build a policy rule needs — see [`SAD.md` §9](SAD.md#9-design-vs-implementation), [`LLD.md` §8](LLD.md#8-agent-background-loops), and `docs/03-architecture/enList-v3-Design.md` §11 step 5. Defined once in `Enlist.ControlPlane.Contracts` and shared by the control plane (detection and validation), the agent (runner-bin selection), and `enlist-deploy` (which prints it), so all three agree on the exact string values.

- **Detected server-side at upload**, from the package itself — the presence of any `*.deps.json` entry means `net10.0`, its absence `net472`. There is no CLI flag for the FLAVOR: `enlist-deploy` takes `--control-plane`, `--app`, `--source` and `--api-key` (or `ENLIST_API_KEY`), and prints the detected flavor for confirmation. A policy rule inherits its package's detected flavor, and a rule claiming a contradicting one is rejected.
- **`enlist-agent --legacy-runner-bin <path>`** registers a second runner build under `net472`, alongside the always-required `--runner-bin` (registered under `net10.0`). One agent process can host both flavors side by side — this is a per-application routing decision at start time, not a separate agent identity.
- A rule naming a flavor the agent has no runner-bin configured for is marked `Failed` immediately (not retried — a missing runner-bin is a configuration problem, not a transient one) with a log line naming the missing flavor and every flavor the agent *does* have configured.
- Changing `RuntimeFlavor` on a running rule (via `PUT`) is treated exactly like changing `Path`/`CronOverrides` — it triggers a stop-and-restart under the newly-selected runner.

### 7.2 Isolation

```csharp
record IsolationSpec(
    string Mode,                                    // IsolationModes.Process | IsolationModes.Container
    string? Image = null,                           // overrides the agent's --container-image for this one rule
    IReadOnlyList<PortMapping>? Ports = null,
    IReadOnlyList<string>? Networks = null,
    IReadOnlyDictionary<string, string>? Env = null);

record PortMapping(int ContainerPort, int? HostPort = null, string Protocol = "tcp", string? Name = null);
```

Isolation is a property of the **policy rule**, not of the package — the same digest runs as a process on one agent and in a container on another, with no rebuild. `null` means `process`.

**Validation, all returning 400:**

| Rule | Why |
|---|---|
| `Mode` must be in `IsolationModes.All` | — |
| `ContainerPort` in 1–65535 | Caught here rather than at `docker run`, whose own error names neither the rule nor the application |
| `HostPort`, if given, in 1–65535 | The message names the better option: *"Omit it entirely"* to let the engine allocate |
| `Protocol` must be `tcp` or `udp` | — |
| **`net472` + `container` is rejected** | The container image is Linux; a .NET Framework application cannot run in it. Enforced on create **and** on an update that only changes the package, since that can change the detected flavor underneath an existing container rule |
| No two rules may claim the same static `HostPort` on an agent both match | A dynamic (`null`) host port cannot collide by construction, so only static ones are checked — treating dynamic as a conflict would condemn the very configuration that makes one rule work across many agents |

A **dynamic** host port (omit `HostPort`) is the recommended configuration: a single hard-coded port could not serve a rule that matches several agents. The engine-allocated port is reported back as a resolved endpoint — see §5a.

---

## 8. Status Codes Summary

| Code | Meaning across this API |
|---|---|
| 200 | Successful read, or a write that returns the affected resource. |
| 201 | Application policy created (`Location` header points at it). |
| 202 | Accepted for asynchronous/best-effort processing (status reports, log batches). |
| 204 | Successful write with nothing to return (delete, scheduling toggle, command acceptance). |
| 400 | Request violates a validation rule (mutual exclusivity, invalid enum value, a malformed digest or application name, a rule naming a digest that is not stored). |
| 401 | No credential, or one that is unknown, revoked, expired or used up (`WWW-Authenticate: Bearer`). Only under `Authentication:Mode=Required`; see §0. |
| 403 | A valid credential of the wrong kind, name or role for this endpoint — an agent on another agent's route, a Viewer on a write, a join token anywhere but enrollment. |
| 404 | Referenced agent / policy rule / package digest does not exist. |
| 409 | Write blocked by a referential-integrity-style business rule — in practice only deleting a package that a policy rule still references. |
| 413 | Package upload larger than `PackageStorage:MaxUploadBytes` (default 512 MB). |
| 503 | `GET /health` only: the control plane is up but its database did not answer. The body is still a `HealthDto`. |

---

## 9. Client Libraries in This Repository

| Consumer | File | Notes |
|---|---|---|
| Enlist.Portal | `src/Enlist.Portal/Services/ControlPlaneApiClient.cs` | Full read/write surface the UI needs; also declares its own local mirror of `AgentStatusSnapshot` (deliberately decoupled — see [`Database-Design.md` §5](Database-Design.md#5-json-in-column-fields--rationale)). |
| Enlist.Agent | `src/Enlist.Agent/Configuration/ControlPlaneAssignmentSource.cs`, `Status/ControlPlaneStatusReporter.cs`, `Logging/ControlPlaneLogForwarder.cs` | Split across three focused clients rather than one shared client class. |
| Enlist.Deploy | `src/Enlist.Deploy/Program.cs` | Raw `HttpClient`, no wrapper — a thin, disposable CLI. |
