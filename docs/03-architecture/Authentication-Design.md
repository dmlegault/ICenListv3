# enList Authentication and Authorization — Design (review finding C2)

**Product:** enList v3
**Document status:** Design, decided 2026-09-11 and **not yet implemented**. Closes review finding **C2** ([`enList-v3-Review-2026-09-09.md` §3.2](../06-background/enList-v3-Review-2026-09-09.md)), the one open item in that review: *no authentication or authorization on any surface*. C2 stays open in the review until this is built; this document is what "built" means.

**The four decisions this rests on**, taken on 2026-09-11 after discussion, with the alternatives they rejected:

| Decision | Chosen | Rejected |
|---|---|---|
| How humans authenticate | **Windows authentication** on the portal, authorization by Windows group | A local user/password store with reset flows; OIDC in v1 |
| How agents get a credential | **Join-token enrollment**: an agent exchanges a short-lived join token for its own long-lived credential | An operator pasting a per-agent secret into each install |
| Where TLS terminates | **Kestrel**, on the control plane and the portal | A reverse proxy as the only TLS point |
| Can authentication be turned off? | **Only when every listener is bound to loopback** — a hard rule with no override | A configuration flag that could disable it on a network |

---

## 1. What this closes

The review named two attacks and the documents implied two more. All four are closed by the same small set of rules:

| Vector | Closed by |
|---|---|
| Anyone who can reach the control plane can **upload a package every agent will run** | Package upload, policy changes, tags, scheduling and commands require a **management credential with the Operator role** (§7) |
| Anyone can **join any agent's push group** on the hub and receive its policy changes | Joining a group requires an **agent credential bound to that agent's name**; the hub checks the name in the request against the name in the credential (§4.5) |
| Anyone can **report status or forward logs as any agent**, corrupting what the portal shows | Same name binding: an agent's credential lets it act as itself and nothing else |
| The whole thing sits on a **"trusted network" assumption** nobody has to write down | Authentication is **on by default** and can only be off on loopback (§8). An unauthenticated control plane reachable from a network cannot be configured; the process refuses to start |

Out of scope, and deliberately unchanged: the runner↔agent control channel (pipes, Unix sockets, a loopback-only published port for containers). It has no authentication and needs none, because it is confined to the host by construction — the review agreed, and nothing here touches it.

---

## 2. Principles

- **Two trust relationships, two mechanisms.** Machines (agents) and humans (operators) never share a credential type, a table, or a code path. Conflating them is how "the portal's key can pretend to be an agent" happens.
- **No identity provider of our own.** enList never stores a human password, never resets one, never has a user-administration page. Windows already does all of that for the people who run this.
- **Safe by construction, not by configuration.** The dangerous state — unauthenticated and reachable — is made impossible at startup rather than discouraged in a document.
- **Secrets are shown once and stored hashed.** Every token and key is a random value the control plane keeps only a SHA-256 of. Losing one means issuing another, never recovering it.
- **Availability over strictness on the agent.** An agent whose credential stops being accepted keeps running what it already runs and complains loudly; it does not tear down production applications because the control plane rejected a header.

---

## 3. Principals and credentials

| Principal | Credential | Issued by | Lives | Used for |
|---|---|---|---|---|
| **Agent** | *Agent token* — `enla_` + 32 random bytes, base64url | The control plane, at enrollment | On the agent, DPAPI-protected file under `--data` (§4.3) | Every agent-facing call and the hub connection, bound to one agent name |
| **Join token** | `enlj_…`, short-lived, optionally multi-use | An Operator (portal, or the CLI verb in §10) | In the operator's hands until used; the installer's Agent page | Exchanged once for an agent token. Never used for anything else |
| **Management API key** | `enlk_…`, with a role | An Operator (portal or CLI verb) | With the tool that holds it: `enlist-deploy`, a CI pipeline | Management endpoints, at the key's role |
| **Portal service key** | An ordinary management API key named `portal`, role Operator | The installer, via the CLI verb, at install | In the portal's configuration, DPAPI-protected (§6.1) | The portal's own calls to the control plane |
| **Operator / Viewer (human)** | Their **Windows identity** | Active Directory or the local machine | Nowhere in enList | The portal, and only the portal |

Prefixes are there so a secret scanner, a log, or an error message can say *what kind* of token was pasted in the wrong place without revealing it. All three are presented the same way: `Authorization: Bearer <token>`.

**Claims** a validated credential produces, which everything in §7 keys on: `enlist:kind` (`agent` | `management`), `enlist:agent` (the agent name, agents only), `enlist:role` (`Operator` | `Viewer`, management only), and `enlist:credential` (the agent name or key name, for audit).

---

## 4. Agents: enrollment with a join token

### 4.1 The flow

1. An Operator creates a **join token**: expiry (default 24 h), and either single-use or a use count (a fleet of 50 gets one token with 50 uses). It is shown once.
2. A new agent is started with that join token — by the installer's Agent page (§14), or with `--join-token` by hand.
3. On first contact the agent calls `POST /api/agents/enroll` with `{ "agentName": "WEB-07" }` and the join token as its bearer. The control plane validates the join token (exists, not expired, not revoked, uses remaining), **refuses if the name already holds a live credential** (HTTP 409 — see 4.4), decrements the use count, creates or updates the agent's registry row, mints an **agent token**, stores its hash in `AgentCredentials`, and returns the token once.
4. The agent stores the token (4.3) and discards the join token. Every subsequent call — policies, reports, logs, capabilities, package downloads, the hub — carries the agent token.

### 4.2 Why this shape

A join token is the thing an operator hands to a machine they have not met yet; the agent token is the thing that machine keeps. Separating them means the long-lived secret is generated on the machine that will hold it and never travels through an installer property, a script, or a ticket. It also means silent fleet installs are one property, `AGENT_JOINTOKEN`, rather than fifty pasted secrets.

### 4.3 Where the agent keeps it

`<data>\credential` — the agent's `--data` root, next to `Packages\`, `Runners\` and `Logs\` — written with **DPAPI at machine scope** (the agent runs as a service, so user scope is unavailable) and an **ACL of SYSTEM, Administrators and the service account**. Machine-scope DPAPI is decryptable by any process on that machine; the ACL is the actual control, and both are applied. The token never appears on a command line: `sc qc` shows a service's arguments to anyone who can query it. `--join-token` exists for manual starts and is consumed on the first run; the agent logs a warning if it is still present on a later start, because that means someone left a join token in a service definition.

### 4.4 Names, duplicates and revocation

An agent name holds at most one live credential. Enrolling a name that already has one is a **409 with a message naming the conflict**, not a silent second registration. This turns the failure recorded in the Runbook — two agents sharing a name, each overwriting the other's reports, indistinguishable from stale portal data — into an error at the moment it is about to happen. To move an agent to a new machine, an Operator revokes the old credential first (portal, Agents tab → *Revoke credential*; or `DELETE /api/agents/{name}`, which revokes as part of deregistering).

A revoked or unknown token is a **401**. The agent treats that as described in §2: it logs through its `FailureNotice` path (once, then rate-limited), **keeps every application running**, and keeps retrying with backoff — the same posture it already takes for a control plane that is simply down. Recovery is a new join token and a re-enrollment; nothing running is disturbed.

### 4.5 The hub

`ApplicationPolicyHub` is reached over the same authentication: the agent's `HubConnectionBuilder` supplies the agent token through `AccessTokenProvider`, which SignalR sends as the `access_token` query parameter on connect — the standard mechanism, and the one place the bearer handler also reads the query string (only on the hub path). `JoinAgentGroup(agentName)` compares `agentName` to the `enlist:agent` claim on `Context.User` and refuses any other name. Pushes originate from the server; no client can send to a group.

---

## 5. Humans: Windows authentication on the portal

### 5.1 Mechanism

The portal enables **Negotiate** (`Microsoft.AspNetCore.Authentication.Negotiate`; Kerberos in a domain, NTLM against local accounts on a workgroup machine) and requires an authenticated user for every page. Browsers on an intranet send Windows credentials to a trusted-zone site automatically, so an operator in a domain opens the portal and is simply in. There is no login page to build, no password to store, no reset flow, no user list.

### 5.2 Roles are groups

Two configuration values, both Windows group names:

```jsonc
"Authentication": {
  "Windows": {
    "OperatorsGroup": "CORP\\enList Operators",
    "ViewersGroup":   "CORP\\enList Viewers"
  }
}
```

Membership of the Operators group is the `Operator` role (everything); the Viewers group is `Viewer` (every page, no write controls). A user in neither sees an access-denied page that names the two groups. Windows group membership arrives as role claims on the principal, so the two policies are one `RequireRole` each. Adding a role later is adding a group name.

The portal **enforces roles itself** — hides and refuses write actions for Viewers — because it is the only path a human takes and it is a trusted component (5.3). Its own call to the control plane carries an Operator-tier key regardless of who is looking at the page.

### 5.3 Deployment notes that will otherwise cost a day

- **HTTPS.** The portal serves humans over TLS at Kestrel (§9); Negotiate over plain HTTP is possible and wrong.
- **SPN.** Kerberos needs a service principal name for the account the portal service runs as. Under *Network Service* or *Local System* the machine account already has `HOST/` SPNs and nothing is needed. Under a dedicated domain account, register `HTTP/<portal host FQDN>` for that account (`setspn -S`), or Negotiate silently falls back to NTLM and then fails across machines. The installer's Portal page says this when *This account* is chosen.
- **Browsers.** The portal's URL must be in the intranet zone (or explicitly trusted) for credentials to be sent automatically; otherwise the browser prompts, which works but looks broken.
- **Later:** an OpenID Connect provider (Entra ID, etc.) is an additional authentication scheme in configuration, not a redesign. Not in v1.

---

## 6. Management callers

### 6.1 The portal → the control plane

A `DelegatingHandler` on the portal's typed `ControlPlaneApiClient` adds two headers to every request: `Authorization: Bearer <portal key>` and `X-Enlist-Operator: <DOMAIN\user>` (the Windows identity of the person whose action this is). The control plane authenticates the first and **logs** the second on every write — it does not authorize on it, because the portal has already done that (5.2). The portal key is an Operator API key named `portal`, created by the installer (§10), stored in the portal's `appsettings.json` as a DPAPI machine-scope blob with the same ACL discipline as §4.3. **It is a root-equivalent secret** and Deployment-IaC will say so.

### 6.2 `enlist-deploy`, CI, scripts

`enlist-deploy` gains `--api-key` (or `ENLIST_API_KEY` in the environment, which is what a pipeline uses; the command line is visible in process listings). Keys are created by an Operator, named (`ci-main`, `robert-laptop`), carry a role, expire by default (90 days, configurable, or never for a service key), and are revocable by name. `GET /api/packages` and friends with a Viewer key is how a read-only dashboard or a reverse proxy's endpoint feed (`/api/endpoints`) authenticates.

### 6.3 Audit

Every write on the control plane logs, structured, **who**: the agent name, or the key name plus the `X-Enlist-Operator` value when present. In v1 that is the existing logging pipeline (and the Windows Event Log under the SCM); an audit table is listed in §16 as the natural next step, not built here.

---

## 7. Authorization, endpoint by endpoint

Three policies cover everything. **Agent(name)** — kind `agent` and `enlist:agent` equal to the `{name}` / `{agentName}` route value, enforced by one endpoint filter that compares the two so no handler has to remember. **Viewer** — kind `management`, any role. **Operator** — kind `management`, role `Operator`.

| Endpoint | Policy | Note |
|---|---|---|
| `GET /health` | **Anonymous** | By design; it now also reports the authentication mode (§8) |
| `POST /api/agents/enroll` | **Join token** | The one endpoint a join token can call |
| `GET /api/agents/{agentName}/policies` | Agent(name) | |
| `POST /api/agents/{agentName}/report` | Agent(name) | |
| `POST /api/agents/{agentName}/logs` | Agent(name) | |
| `PUT /api/agents/{name}/capabilities` | Agent(name) | |
| `GET /api/packages/{digest}` | Agent **or** Viewer | Agents download what they are assigned; operators may too |
| `GET /api/packages/{digest}/manifest` | Viewer | |
| `GET /api/packages` | Viewer | |
| `POST /api/packages` | **Operator** | *"upload code every agent will run"* |
| `DELETE /api/packages/{digest}` | **Operator** | |
| `GET /api/agents`, `GET /api/agents/{name}` | Viewer | |
| `GET /api/agents/{agentName}/report/latest` | Viewer | |
| `GET /api/agents/{agentName}/logs` | Viewer | |
| `PUT /api/agents/{name}/tags` | **Operator** | Changes what runs where |
| `PUT /api/agents/{name}/scheduling` | **Operator** | Stops everything on an agent |
| `DELETE /api/agents/{name}` | **Operator** | Also revokes the agent's credential |
| `POST /api/agents/{name}/commands` | **Operator** | Start/stop on a live application |
| `GET /api/application-policies` | Viewer | |
| `POST`, `PUT`, `DELETE /api/application-policies…` | **Operator** | |
| `GET /api/endpoints`, `GET /api/endpoints/traefik` | Viewer | A reverse proxy holds a Viewer key |
| `ApplicationPolicyHub` connect | Agent | Group join additionally checks the name (4.5) |
| Key and join-token management (new, §10) | **Operator** | Creating a key or a join token, listing them, revoking |

Reads are Viewer, writes are Operator, agents act as themselves. If a future endpoint does not fit one of those three sentences, that is the review comment.

---

## 8. Modes, and the hard rules

One setting on each of the control plane and the portal:

```jsonc
"Authentication": { "Mode": "Required" }   // Required (default) | Off
```

Enforced **at startup**, before a single request is served:

- **R1 — `Off` is honored only on loopback.** If any configured listener is bound to anything other than `127.0.0.1`, `::1` or `localhost`, `Mode: Off` is a startup failure with a message that says exactly this. There is no override, by decision. This is what lets the demo (`start-demo.ps1`, everything on `localhost`) run unchanged, and what makes the *trusted network* paragraph in Deployment-IaC unnecessary rather than merely advisory.
- **R2 — `Required` refuses plain HTTP off loopback.** A bearer token over HTTP on a network is a credential broadcast; a non-loopback `http://` listener in `Required` mode is a startup failure. Loopback HTTP is allowed (a local tool, the demo).
- **R3 — the portal has the same two rules**, with Windows authentication in place of bearer tokens.
- **The default is `Required`.** A control plane with no `Authentication` section at all is authenticated. Turning it off is an explicit, loopback-only act.

`GET /health` reports the mode (`"authentication": "Required"`), so an agent starting with no credential, and the installer's *Verify*, can tell whether one is needed before failing.

---

## 9. TLS at Kestrel

Both servers terminate TLS themselves, using standard Kestrel configuration — the certificate from the **machine certificate store by thumbprint** (what the installer collects) or a PFX path and password:

```jsonc
"Kestrel": {
  "Certificates": {
    "Default": { "Store": "My", "Location": "LocalMachine", "Subject": "…" }   // or "Thumbprint" / "Path"+"Password"
  }
}
```

with the listener `--urls https://+:5293` (control plane) and `https://+:5231` (portal). Agents validate the certificate against the **machine trust store**, so an organisation CA-issued certificate needs nothing on the agent side. For a self-signed or private-CA certificate, `--control-plane-ca <path>` on the agent adds a trust anchor for that connection only — an optional hardening/convenience, not a requirement. A reverse proxy in front remains possible (proxy → Kestrel over HTTPS) and the endpoint feeds exist for it; TLS is simply not delegated to it.

Certificate renewal is an operational task (§16), not something enList automates.

---

## 10. Bootstrap: where the first key comes from

The first Operator key cannot be created by an Operator who does not yet exist. The answer is a **management CLI on the control plane executable itself**, which talks to the database directly and requires no HTTP credential — exactly the posture migrations already have (run out-of-band, by someone with database access on the control plane host):

```
Enlist.ControlPlane.exe create-api-key   --name portal --role Operator [--expires never]
Enlist.ControlPlane.exe create-join-token [--expires 24h] [--uses 50]
Enlist.ControlPlane.exe revoke-api-key    --name ci-main
Enlist.ControlPlane.exe revoke-agent      --name WEB-07
Enlist.ControlPlane.exe list-keys | list-join-tokens
```

Each prints its secret once. The installer uses `create-api-key --name portal` to mint the portal's key and stores it (6.1). Day to day, Operators do the same things from the portal (*Agents → Enroll agent* for join tokens; a *Keys* page under settings), which call the Operator-only management endpoints in §7.

---

## 11. Data model and token format

Three tables, one migration:

| Table | Columns |
|---|---|
| `AgentCredentials` | `AgentName` (PK, FK → `Agents`), `TokenHash` (unique), `IssuedAtUtc`, `LastUsedAtUtc`, `RevokedAtUtc` |
| `JoinTokens` | `Id`, `TokenHash` (unique), `CreatedBy`, `CreatedAtUtc`, `ExpiresAtUtc`, `UsesRemaining` (null = unlimited until expiry), `RevokedAtUtc` |
| `ApiKeys` | `Id`, `Name` (unique), `KeyHash` (unique), `Role`, `CreatedBy`, `CreatedAtUtc`, `ExpiresAtUtc` (null = never), `LastUsedAtUtc`, `RevokedAtUtc` |

Tokens are 32 bytes from a cryptographic RNG, base64url, prefixed (`enla_`, `enlj_`, `enlk_`); only `SHA-256(token)` is stored, and lookup is by hash, so a database read cannot yield a usable credential. `LastUsedAtUtc` is updated at most once a minute per credential to keep the write off the hot path. [`Database-Design.md`](Database-Design.md) is updated when the migration lands.

---

## 12. What changes, by component

| Component | Change |
|---|---|
| **Control plane** | A bearer `AuthenticationHandler` (validates the three token kinds by hash, emits the claims in §3, reads `access_token` on the hub path); the three policies and the name-binding endpoint filter (§7); `POST /api/agents/enroll` and the key/join-token management endpoints; the startup rules (§8); the mode in `/health`; the CLI verbs (§10); the migration (§11); audit logging on writes (6.3). `JoinAgentGroup` gains the name check (4.5) |
| **Agent** | Credential load/save with DPAPI + ACL (4.3); enrollment on first start; `--join-token` and `--control-plane-ca`; one `DelegatingHandler` shared by the three `HttpClient`s in `ControlPlaneAssignmentSource`, the status reporter and the log forwarder; `AccessTokenProvider` on the hub connection; 401 handling through `FailureNotice` without touching running applications (4.4) |
| **Portal** | Negotiate + `[Authorize]` app-wide; the two group-to-role policies; write controls hidden and refused for Viewers; the `DelegatingHandler` on `ControlPlaneApiClient` (6.1); *Enroll agent* and *Keys* UI; an access-denied page naming the groups |
| **`enlist-deploy`** | `--api-key` / `ENLIST_API_KEY` |
| **Contracts** | `EnrollAgentRequest/Response`, `CreateApiKeyRequest/Response`, `CreateJoinTokenRequest/Response`, the role constants, and `authentication` on `HealthDto` |

Nothing in `AgentHost`, the runners, or the seam changes. Authentication is a boundary concern and stays at the boundary.

---

## 13. Rollout

1. **Infrastructure first** (control plane only): handlers, policies, tables, the mode switch and its hard rules, `/health` reporting the mode, the CLI verbs. Ships with `Mode` honoured as configured. An existing deployment that is network-exposed can no longer start with `Off` — that is the intended pressure; a loopback demo is untouched.
2. **Agents**: the enrollment endpoint and the agent's credential handling. Upgrading a fleet: create a join token, redeploy each agent with it (the installer's Agent page, or `--join-token`), then set the control plane to `Required`. Until then agents without credentials still work because the mode is what decides.
3. **Portal and tools**: Windows authentication and roles, the portal key, `--api-key` on the CLI, audit.
4. **Documents**: an authentication section in [`API-Specification.md`](API-Specification.md); TLS, groups and keys in [`Deployment-IaC.md`](../05-operations/Deployment-IaC.md), which also loses its trusted-network preamble; Runbook entries for a revoked agent and a lost portal key; the installer pages (§14); the review's C2 marked resolved.

The order matters: step 1 is safe to ship on its own, step 2 makes the flip to `Required` possible, step 3 is what makes the portal usable once it is required.

---

## 14. What this changes in the installer design

[`Installer-UI-Design.md`](../05-operations/Installer-UI-Design.md) was written around C2's absence and reserved space for it. With the decisions above:

- The reserved **initial administrator page is not needed**. Humans authenticate with Windows, so the **Portal page asks for the Operators and Viewers group names** — and no password, ever.
- The **Agent page gains a *Join token* field** (`AGENT_JOINTOKEN`). The installer performs the enrollment itself (it is elevated and on the network), writes the resulting agent token to the credential file with the right ACL, and the service starts already enrolled. A bad join token fails the install before any service is created — earlier and clearer than a service that starts and then logs 401s. *Verify* first checks `/health`, which now says whether a token is needed.
- The **Control Plane and Portal pages gain a TLS certificate** (a thumbprint from the machine store, or a PFX), and their listen defaults become `https://`. R2 makes this non-optional off loopback.
- The installer creates the **portal's key** with the CLI verb (§10) and stores it in the portal's protected configuration.
- The **Demo** install type is unaffected: it binds to loopback and runs with `Mode: Off`, which is exactly the case R1 permits — the rule, not an exception to it.

---

## 15. What pins it down (tests)

Control plane: every non-`/health` route is 401 without a credential in `Required` mode; startup refuses `Off` off loopback and refuses plain HTTP off loopback in `Required`; an agent token for `WEB-07` on a `WEB-08` route is 403, and a hub join for another name is rejected; a revoked token is 401; a join token enrolls once, then is 401, expires, and returns 409 for a name that already holds a credential; a Viewer key on a write is 403 and on a read is 200. Agent: enrolls and persists on first start, starts with a stored credential, keeps its applications running through a 401 and recovers on re-enrollment. Portal: the handler attaches the key and the operator header; a Viewer sees no write controls and a write attempt is refused server-side. Deploy: `--api-key` reaches the control plane. All against the real control plane and real database, in the existing test servers' style.

---

## 16. Deliberately later

- **mTLS for agents.** Certificate lifecycle across a fleet is its own project; bearer-over-TLS is the standard shape and adequate. Nothing here prevents adding it.
- **OpenID Connect** for the portal (Entra ID and others): an additional scheme in configuration.
- **Per-application permissions.** Two roles are enough until someone shows the case that needs a third.
- **An audit table** with a portal view, replacing the structured log in 6.3.
- **Certificate renewal** automation.
- **Key rotation UI.** Revoke-and-create covers it; a one-click rotate is convenience.

---

*Related:* [`enList-v3-Review-2026-09-09.md` §3.2](../06-background/enList-v3-Review-2026-09-09.md) (the finding) · [`API-Specification.md`](API-Specification.md) (the endpoints in §7) · [`Installer-UI-Design.md`](../05-operations/Installer-UI-Design.md) and [`Demo-Install-Design.md`](../05-operations/Demo-Install-Design.md) (what §14 changes) · [`Deployment-IaC.md`](../05-operations/Deployment-IaC.md) (TLS, groups, keys once built).
