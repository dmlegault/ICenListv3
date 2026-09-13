# Functional Requirement Specification (FRS)

**Product:** enList v3
**Document status:** Derived from the current implementation. Originally 2026-09-03; **FA-1 through FA-7, FA-11 and FA-13 rewritten 2026-09-08** against the shipped portal after an audit found this document still describing the pre-restructure master-detail UI (`AppInstancePanel`, `ApplicationDetailPanel`), an `enlist-deploy` CLI with four flags that no longer exist, an explicit-agent targeting mode that was removed, and a 409 on agent delete that is no longer returned. Complements [`SRS.md`](SRS.md) (system-level "shall" statements) with concrete, use-case-level functional flows as implemented in Enlist.Portal, Enlist.Deploy, Enlist.Agent, and Enlist.ControlPlane.

---

## 1. Actors

- **Operator** — uses Enlist.Portal in a browser.
- **Deployer** — runs `enlist-deploy` (interactively or from a script/CI job).
- **Viewer** — uses the portal too, and may change nothing. Every write control is hidden from them rather than disabled (FA-14).
- **Agent** — the automated actor running on every managed host; several functions below describe the agent’s own behavior, not a human’s.

---

## 2. Functional Areas

### FA-1: Deploy an Application

**Actor:** Deployer
**Entry point:** `enlist-deploy` CLI (`src/Enlist.Deploy/Program.cs`)

**Flow:**

```
enlist-deploy --control-plane <url> --app <name> --source <dir>
```

Those three flags are the CLI's **entire** surface. It uploads a package and nothing else.

1. The CLI zips `--source` (no base directory entry).
2. It POSTs the zip to `/api/packages?application=<name>`. The control plane computes the SHA-256 digest server-side; if that digest already exists, nothing is re-uploaded (`AlreadyExisted: true`). The name has to be a valid application name (letters, digits, `-`, `_`, `.`; starting with a letter or digit; at most 64) — it becomes a folder and a process name on every agent — and the CLI checks it before sending; the control plane refuses anything else with a 400 the CLI prints verbatim.
3. The control plane **detects the runtime flavor from the package itself** — any `*.deps.json` entry means `net10.0`, its absence `net472`. There is no `--runtime` flag; the CLI prints what was detected, for confirmation.
4. The CLI prints the digest and exits.

**`enlist-deploy` creates no placement.** Declaring *where* an application runs is a separate, deliberate act performed in the portal (or against `/api/application-policies`), so a redeploy cannot silently change placement. Nothing is pushed to any agent by this step — uploading a package changes no policy, so no agent has anything to reconcile.

**Postcondition:** the package exists and is visible on the Applications tab — an application appears there as soon as a package has been uploaded for it, *before* any rule says where it runs. "Roll back to an older digest" is then an edit to a policy rule's `PackageDigest`, not a re-run of this command.

**Rollback:** point a policy rule at an older digest — `PUT /api/application-policies/{id}` with a previous `PackageDigest`, or the same edit in the portal. There is no "rollback" verb, and re-running `enlist-deploy` is not one: it uploads, and uploading changes nothing about what is running. Every previously-uploaded digest stays available until package retention collects it (30 days by default, and never while a rule still references it).

**Placement is FA-4's job.** Declaring a rule is what causes the control plane to push `ApplicationPoliciesChanged` to the affected agents; they then reconcile within moments, or on next reconnect if offline (see FA-9).

---

### FA-2: View Fleet-Wide Application Status

**Actor:** Operator
**Entry point:** Applications page (`/`, `Components/Pages/Applications.razor`) — the portal's landing page.

**Flow:**

1. The page lists every application **a package has been uploaded for** — not only those with a rule saying where they run, so a freshly-deployed application is visible before it has been placed. Each row shows its description (if the plugin declares `[EnlistApplication(Description=...)]`), its version, a rule count linking to the policy screen, and the number of agents it currently resolves onto.
2. Expanding a row (`RunningInstancesTable`) reveals a flat, sortable table **in place** — one row per (service-or-job × agent), with columns Name / Type / Agent / Qualified via / Endpoint / State / action. "Qualified via" names the tag that matched, so *why* this application landed on this agent is answerable without opening the rule.
3. The action per row is Stop/Start for a service and Disable/Enable for a job, gated per row on that agent's liveness and the application's state there (see [`LLD.md` §11](../03-architecture/LLD.md)).
4. A "Logs:" strip above the table offers one chip per agent the application runs on, opening `LogTailView` scoped to that (agent, application) pair.
5. The panel polls while open, so a crash, a job firing, or a command taking effect appears without a manual refresh.

> Master-detail selection was replaced by expand-in-place in the five-tabs-to-three restructure; the former `ApplicationDetailPanel` / `AppInstancePanel` cards with their App Services / Scheduled Jobs / Logs / Info tabs no longer exist.

---

### FA-3: View Fleet-Wide Agent Status

**Actor:** Operator
**Entry point:** Agents page (`/agents`, `Components/Pages/Agents.razor`)

**Flow:**

1. The page lists every registered agent with its tags, computed Applications and Policy Rules counts (resolved the same way as FA-2, so the two pages cannot disagree), last-seen time, Online/Offline status, and a Scheduling toggle (FA-13).
2. An agent that reports a usable container engine carries a **containers** chip. This reflects a real probe result — `docker version` against the *server* — re-run on every heartbeat, not the fact that `--container-image` was passed. A flag proves someone typed it; the chip proves an engine answered.
3. Expanding a row reveals `AgentRunningApplicationsTable`: what that agent is running, with the application, version, how it qualified, and state.

**This view is deliberately read-only** — no Start/Stop, not even per service. Actions live on the Applications tab, where the unit of action is the application rather than the host. An operator asking "what is this machine doing?" is asking a different question from "change what this application does", and mixing them invites a change aimed at one application being made from a page organised around a different noun.

---

### FA-4: Enable / Disable an Application Policy Rule

**Actor:** Operator
**Entry point:** The per-application **policy screen** (`/applications/{AppName}/policy`, `Components/Pages/ApplicationPolicyScreen.razor`), reached from the rule count on an Applications row.

**Flow:**

1. The screen lists every rule for this application — its tag selector, package version, desired state — with edit/delete, and an "+ Add rule" wizard (`ApplicationPolicyWizardDialog`: what runs → which agents qualify → confirm, with a live "qualifies N agents right now" preview).
2. Toggling a rule's state calls `ApplicationPolicyStateToggler`, which **always** resolves and names the agents the selector currently matches, shows a confirmation dialog naming them with a caveat that current *and future* matching agents are affected, and only then applies the change (see [`LLD.md` §12](../03-architecture/LLD.md)).
3. The control plane persists the new `DesiredState` and notifies the affected agents over SignalR.
4. Each agent reconciles: starting stages a runner and starts every declared service, registering every job with a resolvable cron; stopping tears down the runner process entirely — not merely its services — and reports every service `Stopped` in its next snapshot.

**A rule's enable/disable state lives only here**, deliberately not duplicated as a shortcut on the Applications tab. The blast radius of a rule is not visible from a row: `env=prod` reads as one line and may govern forty agents.

**Precondition:** none beyond a rule existing. This is declarative and does not require any matching agent to be reachable — an offline agent applies it on reconnect (FA-9).

---

### FA-5: Start / Stop a Service, Enable / Disable a Job (imperative)

**Actor:** Operator
**Entry point:** The per-row action in an expanded application on the Applications tab (`RunningInstancesTable`) — Stop/Start for a service row, Disable/Enable for a job row.

**Flow:**

1. The button sends `POST /api/agents/{name}/commands` with `{ ApplicationName, TargetKind: "Service"|"Job", TargetName, Action: "Start"|"Stop" }`.
2. The control plane validates the agent is registered and the kind/action are recognized, then relays the command fire-and-forget over the agent's SignalR group — there is no acknowledgement; a snackbar confirms only that the request was accepted for delivery, not that it took effect.
3. The agent's `CommandReceived` handler acts on the command. A **service** action is forwarded straight to the runner process hosting that application. A **job** action is mostly agent-side: `JobScheduler.Register`/`Unregister` decides whether it fires again.
4. Disabling a job additionally sends the runner a `CancelJobCommand`, which cancels the `CancellationToken` handed to any run of that job currently executing. Both halves are wanted — no more firings, *and* the run underway is asked to unwind — and only the second needs a runner. A job that never observes its token runs to completion regardless; nothing can stop it.
5. The runner reports the resulting state change back up the same path that populates the next status report. A cancelled run reports `Stopped` with `JobResult.Outcome = Cancelled` — its own outcome, not a flavour of failure — which the agent records as that job LastRunOutcome and logs at Warning rather than Error. It did not break, and it also did not finish.

**Precondition:** the button is **disabled** (with an explanatory tooltip) whenever:
- The agent is stale (no recent report — the command would go into a SignalR group nobody is listening in), or
- The application itself isn't `Running` (its runner process doesn't exist, so there is nothing to forward the command to).

---

### FA-6: Manage Agent Tags

**Actor:** Operator
**Entry point:** "Edit tags" icon on an Agents row → `EditTagsDialog`.

**Flow:**

1. Operator edits the key/value tag set and saves.
2. `PUT /api/agents/{name}/tags` upserts the agent row (creating it if this agent was pre-registered before its process ever connected) and pushes `ApplicationPoliciesChanged` to that one machine, since a tag change can newly match or unmatch tag-selector assignments.
3. The agent re-fetches and reconciles.

---

### FA-7: Delete an Agent

**Actor:** Operator
**Entry point:** Delete icon on an Agents row.

**Flow:**

1. A confirmation dialog explains the agent will simply reappear the next time its process reports in.
2. `DELETE /api/agents/{name}` always succeeds for a registered agent — **there is no 409**. Deletion cannot be blocked, because with tags-only targeting no rule *names* an agent: a selector that still matches simply governs one fewer. (The old 409 existed to protect explicit placements, which no longer exist.) `404` if the name was never registered.
3. Historical `AgentReportEntity`/`AgentLogEntity` rows for that agent name are deliberately left in place, not cascade-deleted — there is no FK to `Agents`. They are history keyed by a name string, and if that name registers again (a rebuilt box reusing a hostname) the history is still worth having. They age out on the ordinary retention sweeps like every other agent's (the newest report is always kept).

**Deleting is bookkeeping, not a safety gate.** The row reappears the moment that agent's process reports in again, which is exactly what step 1's dialog says — the useful case is tidying up a decommissioned host, not preventing anything.

---

### FA-8: Manage Packages

**Actor:** Operator / Deployer
**Entry point:** an application’s Packages screen (`/applications/{name}/packages`) and `enlist-deploy`/`POST /api/packages`. There is no site-wide packages page: a package belongs to an application, and the portal only ever shows one application’s.

**Flow:**

- **Upload:** see FA-1, step 2.
- **List:** the application’s Packages screen shows that application’s stored digests with size, upload time and version number.
- **Delete:** blocked with `409 Conflict` if any assignment (Running or Stopped) still references the digest — a Stopped assignment still means "this is what would run if flipped back on," so it still counts as a reference. The portal disables the delete button under the same condition the API enforces.
- **Automatic retention:** independent of manual delete, `PackageRetentionSweepService` marks a package `UnreferencedSinceUtc` the first sweep that finds no assignment pointing at it, and only physically deletes it once that mark has stood for the configured retention period (default 30 days) — so a digest superseded minutes ago remains available for a quick rollback.

---

### FA-9: Reconcile After Reconnect (agent-side)

**Actor:** Agent
**Trigger:** The agent's SignalR connection to the control plane drops and reconnects (control plane restart, transient network issue).

**Flow:**

1. `HubConnectionBuilder.WithAutomaticReconnect()` re-establishes the connection under a new connection ID.
2. The agent re-invokes `JoinAgentGroupMethod` for its own agent name — required because SignalR group membership is scoped to the connection ID, not the machine, and is lost across a reconnect.
3. The agent treats the reconnect itself as "something might have changed while I was down" and triggers an immediate re-fetch/reconcile pass, independent of whether any push was actually missed.

---

### FA-10: Heartbeat (agent-side)

**Actor:** Agent
**Trigger:** A fixed timer (default every 2 minutes), independent of any state change.

**Flow:**

1. The agent sends the same status snapshot shape it would send reactively, even if nothing changed.
2. This bounds how long a healthy, quiescent agent can go without proving it is still alive — without it, the portal's staleness detection would eventually misclassify a perfectly healthy machine as stale simply because nothing had happened recently.

---

### FA-11: Tail Application Logs

**Actor:** Operator
**Entry point:** Logs page (`/logs`), or a per-agent "Logs:" chip above an expanded application on the Applications tab.

**Flow:**

1. Operator selects an agent and an application.
2. The view polls `GET /api/agents/{agentName}/logs?application=<name>&sinceId=<lastSeenId>` every few seconds, appending only rows with `Id` greater than the highest already shown, capped at a maximum displayed line count.
3. Log lines are colored by level and show their originating source (a service/job name, or `"agent"`/`"runner"` for lifecycle messages).

---

### FA-12: Plugin Discovery and Hosting (runner-side, no human actor)

**Trigger:** The runner process starts, pointed at an extracted application directory.

**Flow:**

1. Every `*.dll` in the directory is loaded into a per-application `AssemblyLoadContext` (`PluginLoadContext`).
2. Reflection scans every loaded type for `[EnlistService]`/`[EnlistJob]`, matched by attribute type **name**, not compiled identity.
3. A discovered Service with no `[EnlistStart]` method, or Job with no `[EnlistExecute]` method, produces a warning but does not stop discovery of the rest.
4. An assembly-level `[EnlistApplication(Description=...)]`, if present on any loaded assembly, becomes the application's description; the first one found wins.
5. The runner reports the discovered shape (`ReadyMessage`: services, jobs, warnings, isolation diagnostics, application description) to its parent agent over a named pipe before accepting any Start/Stop/Execute command.

---

### FA-13: Include / Exclude an Agent from Scheduling

**Actor:** Operator
**Entry point:** The Scheduling toggle on an Agents row.

**Flow:**

1. Toggling calls `PUT /api/agents/{name}/scheduling` with `{ SchedulingEnabled: false }`, using the same confirm-then-apply pattern as FA-4 — the dialog names what is about to stop, because the effect is a fleet-visible outage on that host, not a display preference.
2. The control plane persists the flag and pushes over SignalR immediately, so the agent acts at once rather than at its next poll.
3. Policy resolution for that agent short-circuits to an **empty list** (see [`LLD.md` §3.2](../03-architecture/LLD.md#3-tag-selector-matching)).
4. The agent's ordinary reconciliation does the rest: handed an empty list, it tears down everything it is currently running. Re-including it makes every matching rule apply again on the next resolve.

**This needed no agent-side code at all.** "Stop everything and accept nothing" is already exactly what an agent does when its policy list is empty, so exclusion is expressed by making the list empty rather than by adding a new command the agent must understand.

**Distinct from disabling a rule (FA-4):** disabling a rule stops one application *everywhere its selector reaches*; excluding an agent stops *everything* on one host and leaves every rule untouched. The first is about an application, the second about a machine — draining a box for maintenance, most often.

**Precondition:** the agent must already be registered — `404` otherwise. Unlike the tags endpoint, this one does not create the row, since excluding an agent that has never existed has no meaning.

---

### FA-14: Sign In to the Portal

*Added 2026-09-13. Built 2026-09-11; the FRS simply never gained an area for it.*

**Actor:** Operator or Viewer
**Entry point:** opening any portal page.

**Flow:**

1. The portal authenticates with Windows (Negotiate). There is no login form and no password to type: the browser and the host negotiate the identity the person is already signed in as.
2. Their Windows group membership decides their role. One configured group maps to `Operator`, another to `Viewer`.
3. A person in neither group is refused with an explanation, not quietly given the lesser role. Being unable to do anything and not knowing why is the failure this avoids.
4. The app bar names who the portal thinks they are and at what role, on every page. An operator who has been demoted, or who is signed in as the wrong account, should not have to deduce it from a disabled button.

**A `Viewer` sees everything and changes nothing.** Every control that would write is HIDDEN from them (`<AuthorizeView Policy="Operator">`), not disabled. That is the opposite of the stale-agent gating in FA-5, and the difference is deliberate: a Start button greyed out on a stale agent will work again in a minute, so it is worth showing and explaining, while a Delete button a Viewer will never be allowed to press is permanent clutter. Transient unavailability is disabled with a tooltip; permanent unavailability is absent.

**The portal talks to the control plane with its own key**, not with the person's identity — the control plane speaks bearer tokens, not Windows. The key is minted with `Enlist.ControlPlane create-api-key --name portal --role Operator` and given to the portal as `ControlPlane:ApiKey`, protected with `Enlist.Portal.exe protect`. The person's identity travels alongside it in `X-Enlist-Operator`, for the audit line only — see FA-16.

---

### FA-15: Enroll an Agent, and Revoke It

*Added 2026-09-13. Built 2026-09-11.*

**Actor:** Operator
**Entry point:** the Enroll button on the Agents page, and the Access page.

**Flow:**

1. The operator mints a join token. It is shown **once**, in the enrollment dialog, alongside the exact `enlist-agent` command line that consumes it. Only a SHA-256 hash is stored, so there is nowhere to go and look it up later.
2. The agent is started with `--join-token`. It calls `POST /api/agents/enroll`, which spends the token and returns that agent's own credential.
3. The agent stores the credential encrypted under the machine's own key (DPAPI, machine scope) and presents it on every subsequent call, the SignalR hub connection included. `--join-token` is needed once, not on every start.
4. A join token is spendable exactly once, and is charged only after the enrollment is otherwise known to be valid — so a conflicting enrollment does not silently burn it, and two agents racing on the same token cannot both win.
5. The Agents page shows each agent's credential state: holds a live credential, was revoked, or never enrolled — which under authentication `Off` is every agent.

**Revoking takes effect on the agent's next call**, with nothing to restart. A revoked agent keeps running what it already started and stops being able to fetch, report or connect; it says so once in its own log, with the remedy, rather than once per refused call.

---

### FA-16: Manage API Keys, and Read the Audit Trail

*Added 2026-09-13. Built 2026-09-11.*

**Actor:** Operator (a Viewer may do none of it)
**Entry point:** the Access page.

**Flow:**

1. An operator creates a key with a name and one role, `Operator` or `Viewer`. The key is displayed once and stored as a hash.
2. The list shows each key's name, role, creation time and who created it — never the key.
3. Revoking a key takes effect on its next use.

**The audit trail in v1 is the log**, not a table ([`Authentication-Design.md` §6.3](../03-architecture/Authentication-Design.md)). Every administrative write produces one line naming the method, the path, the outcome and who — including a write that **throws**, which is the one most worth having. Reads are not audited, and neither is an agent's own telemetry: status reports, log batches and capability reports arrive every few seconds from every agent and would bury everything else. Enrollment **is** audited, being the one thing a join token does.

**A key's own name identifies it in the audit line.** Where the portal is the caller, the line names both: the portal's key and, from `X-Enlist-Operator`, the person whose action it was. That header is a courtesy, never an authorization input, and is stripped of control characters and length-capped before it can reach a log line or a `CreatedBy` column.
