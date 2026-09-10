# `.demo/` — local runtime state of the demo fleet

**[`demo/start-demo.ps1`](../demo/start-demo.ps1) creates everything here.** It launches the two
demo agents and the control plane, and this tree is their on-disk state: each agent's own working
set plus the captured console output of every process. It is generated, not authored — nothing here
is a source artifact, it is all recreated from scratch on the next run, and the whole tree is safe
to delete when the demo is stopped.

Do not confuse it with [`demo/`](../demo/) (no leading dot), which is the opposite: checked-in
tooling you edit by hand — that start script and [`demo/sql-server/`](../demo/sql-server/) (the SQL
Server on `wslc`).

## Where the demo's real data lives — not here

The fleet's authoritative state — registered agents, package metadata, application policies, status
reports, application logs — lives in the **`EnlistControlPlane` database** on the demo SQL Server
(`127.0.0.1,14330`, in `demo/sql-server/`). This directory is only each agent's *local* working set:
its package cache, its staged runners, and its own file logs. Deleting it loses none of the demo's
data; the agents rebuild it on the next start.

## Layout

```
.demo/
  agent-01/            one agent's data root  (agent started with --data .demo/agent-01)
  agent-02/            the other agent's data root  (--data .demo/agent-02)
  logs/                stdout/stderr of the demo processes, captured by start-demo.ps1
```

### `agent-01/`, `agent-02/` — an agent's `--data` root

Each is what a single agent is given as `--data`. Two separate agents on one machine is the whole
point of the demo: it shows policy targeting, per-agent scheduling, and one application running on
two agents at once. Inside each:

- **`Packages/`** — the content-addressed package cache. One `<digest>/` directory per downloaded
  package (its extracted contents, bind-mounted read-only into a container or loaded in-process), and
  a `<digest>.complete` marker written once the download and extraction finished. **Old file
  timestamps here are normal and not a sign of staleness** — a package is addressed by the hash of
  its bytes, so its files keep whatever date they were built, and the same digest stays valid
  forever. Do not delete a `<digest>/` while the demo is running: a running application may be loaded
  from it.
- **`Runners/<Application>/`** — the runner binaries staged for one process-backend application,
  copied from `--runner-bin` / `--legacy-runner-bin` when the app starts. Container applications
  (e.g. OrderProcessor on DEV-AGENT-02, which runs on `wslc`) stage nothing here — their runner lives
  in the image. Re-staged automatically when the runner build changes.
- **`Logs/`** — this agent's own logs:
  - `agent-<date>.log` — the agent's operational log (reconciliation, crash/restart, orphan reaping).
  - `<Application>/<date>.log` — each application's captured stdout/stderr.
  - `status.json` — the last status snapshot the agent posted.

  A new file is opened per day; **previous days' files are never reopened** and are pure disposable
  history. They were cleared on 2026-09-10; clear them again with, from the repo root:

  ```bash
  find .demo -name '*.log' -not -name "*$(date +%F).log" -delete
  ```

### `logs/` — the demo processes' console output

Created by `start-demo.ps1`, which redirects each process it launches here:

- `control-plane.log` / `control-plane.err.log` — the control plane on `:5293`. When a start fails,
  this is where the reason is (a pending migration is the usual one, since the demo runs as
  Production and verifies the schema rather than applying it).
- `agent-01.log`, `agent-02.log` and their `.err.log` siblings — each agent's console. Sparse:
  the agent's substantive logging goes to `agent-0X/Logs/` above, not here.

## Resetting

- **Throw away one agent's local state:** stop the demo, delete `.demo/agent-01/` (or `-02/`). The
  agent recreates it and re-downloads its packages on the next start.
- **Throw away everything local:** stop the demo, delete all of `.demo/`. Recreated on the next
  `start-demo.ps1`.
- **Throw away the demo's *data* (the database and package blobs):**
  `demo/sql-server/demo-db.ps1 reset -Force` — a different thing entirely, and the only destructive
  step that touches what SSMS shows.
