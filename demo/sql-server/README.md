# Demo SQL Server (WSL container)

A real, persistent SQL Server for **demos**, running in a WSL container through `wslc` — no Docker
Desktop, no daemon, nothing to license. The test suite keeps using LocalDB and is unaffected.

## Why two databases

|  | LocalDB | This container |
|---|---|---|
| Used by | The test suite | Demos, manual exploration |
| Lifetime | One database per test, dropped at the end of the run | Survives reboots until you reset it |
| Connection | `(localdb)\mssqllocaldb`, Windows auth | `127.0.0.1,14330`, SQL auth |
| Why | No install, no daemon, nothing left behind — a test run must not depend on a container engine being up | Looks like production, stays put between sessions, and an audience can watch you query it in SSMS |

The split is enforced by construction, not by convention:
[`ControlPlaneTestServer.cs`](../../tests/Enlist.TestSupport/ControlPlaneTestServer.cs) builds its
connection string in code with `(localdb)\mssqllocaldb` hardcoded, rather than reading
configuration. No environment variable you export for a demo can reach a test run, and running
`dotnet test` never needs a container engine.

## Prerequisites

- **WSL 2.9.11 or later**, which ships `wslc` (`wsl --update` if `wslc version` says otherwise).
  The installer puts it at `C:\Program Files\WSL\wslc.exe` and does not always put it on PATH; the
  script looks there when it is not. This is the same engine the agent's `--container-engine wslc`
  option uses, so a machine set up for containerized applications already has everything.
- ~1.8 GB of disk for the image, ~500 MB for the volume once the demo has data in it.
- Nothing else. In particular, not Docker.

## First run

```bash
cd demo/sql-server
cp .env.example .env
```

Edit `.env` and set `MSSQL_SA_PASSWORD`. SQL Server enforces its own complexity rules (8+
characters, three of upper/lower/digit/symbol) and rejects a weak one **at container start**, which
surfaces as a container that exits rather than a login error — `demo-db.ps1 up` detects that case
and prints the log for you. Keep double quotes out of the password: it travels to `wslc` as a
native-command argument, and Windows PowerShell 5.1 does not escape embedded quotes.

Then:

```bash
./demo-db.ps1 up
```

The first `up` creates the named volume, pulls the image (once, with progress), creates the
container, and blocks until SQL Server actually accepts a login. `wslc` reports the container
running as soon as its process exists, roughly 30 seconds before the server is usable — long enough
that a control plane launched immediately afterward fails to connect, which is why the script waits.

## Pointing the control plane at it

The control plane reads `ConnectionStrings:ControlPlane`, so the standard ASP.NET environment
variable override is all it takes — no code or `appsettings.json` change, and nothing that could
leak into a test run:

```powershell
$env:ConnectionStrings__ControlPlane = ./demo-db.ps1 connstring
dotnet run --project src/Enlist.ControlPlane
```

Leave `ASPNETCORE_ENVIRONMENT` as `Development`. That is what makes the control plane run
`MigrateAsync` on startup, which **creates the `EnlistControlPlane` database and its schema on
first connect** ([`Program.cs:69`](../../src/Enlist.ControlPlane/Program.cs)) — so there is no
separate database-creation step here. Outside Development the control plane deliberately verifies
the schema and refuses to start rather than creating anything, so a demo run set to `Production`
would fail by design; see [`Deployment-IaC.md`](../../docs/05-operations/Deployment-IaC.md) §1.4.

The portal needs no change at all — it talks to the control plane over HTTP and never touches SQL.

For the standard demo fleet, [`demo/start-demo.ps1`](../start-demo.ps1) does all of this in one go: it runs `up`, sets the override, starts the control plane on :5293 (as Production, so it verifies the schema rather than creating it — apply migrations first with `dotnet ef database update`) and starts both demo agents. The demo has run against this server, with the LocalDB rows copied across, since 10 September 2026.

## Everyday use

| Command | Effect |
|---|---|
| `./demo-db.ps1 up` | Create or start, wait until it accepts a login |
| `./demo-db.ps1 down` | Shut SQL Server down cleanly and stop the container. **Data is kept** — `up` brings it back |
| `./demo-db.ps1 status` | Container state, whether it accepts a login, and the databases on the server |
| `./demo-db.ps1 connstring` | Print the connection string |
| `./demo-db.ps1 reset -Force` | Destroy the container, the volume *and* the package blobs, for a demo from zero |

**After a reboot, run `up` again.** `wslc` has no restart policy (Docker's `restart: unless-stopped`
has no equivalent), so the container is simply stopped when WSL comes back. `up` recognises the
existing container and starts it with its volume intact; the data is where you left it.

### Connecting SSMS or Azure Data Studio

Server `127.0.0.1,14330`, SQL Server authentication, user `sa`, the password from `.env`, and tick
**Trust server certificate** — the server generates a self-signed one on first boot.

The port is published on `127.0.0.1` only. That is `wslc`'s default for a published port, and the
right one for a server running as `sa` with its password in a plaintext file.

## Why `reset` also deletes package blobs

Package *bytes* live on the host filesystem under `PackageStorage:Root` (default: `PackageBlobs`
under the control plane's content root), entirely outside SQL Server. Dropping the database alone
leaves them orphaned, and because byte-identical zips hash identically, the next demo's upload
finds its blob **already on disk** while the metadata table has never heard of it — the blob store
and the database disagree from the very first upload.

`ControlPlaneTestServer` hit exactly this and now isolates a blob root per test for the same
reason. `reset` clears both blob directories that exist in this tree (`PackageBlobs/` and
`src/Enlist.ControlPlane/PackageBlobs/`) so the two stay in step. It prints what it will delete and
requires `-Force`.

## What this is *not*

This is a developer-laptop demo server, bound to `127.0.0.1`, running as `sa` with a password in a
plaintext `.env`. That is fine for its purpose and unacceptable for anything else. It is not a
deployment artifact and must not become the basis of one — production runs a database created by a
DBA, schema applied by a migrations bundle under an elevated account, and the application itself on
a read/write login with no DDL rights. See [`Deployment-IaC.md`](../../docs/05-operations/Deployment-IaC.md).

## How `down` stops the server, and what the exit codes mean

PID 1 in Microsoft's image is `launch_sqlservr`, a launcher that ignores SIGTERM. A plain
`wslc stop` therefore waits out its timeout and SIGKILLs the container — `Exited (137)`, no shutdown
in the log, and crash recovery on the next start. So `down` first sends a T-SQL `SHUTDOWN`, which
checkpoints every database and exits in about a second with `Server shut down by request from login
sa` in the log; the container then shows **`Exited (255)`, which is what a clean SQL Server exit
looks like here**. The engine stop is only the fallback for a server that cannot take the request
(still starting, or a changed password).

## Troubleshooting

**"wslc was not found."** WSL is older than 2.9.11, or `wslc.exe` is not on PATH and not at
`C:\Program Files\WSL\`. `wsl --update`, then `wslc version`.

**"wslc is installed but its container service is not answering."** `wslc list` itself fails.
`wsl --shutdown` and try again; `wslc info` prints what the service thinks is wrong.

**The container exits instead of starting.** Almost always the SA password failing SQL Server's
complexity rules. `up` prints the last log lines; `wslc logs enlist-demo-sql` has the rest.

**Port 14330 already in use.** Change `MSSQL_PORT` in `.env`, then `reset -Force` and `up` — the
port is fixed when the container is created.

**`status` says the server is not accepting logins but the container is running.** Either it is
still starting (wait 30 s), or the password in `.env` is not the one the volume was created with —
the SA password lives in the data volume, not in `.env`. Either put the original back, or
`reset -Force` for a fresh server.

**Running the script from Git Bash.** Call it through `powershell.exe -File demo-db.ps1 ...` and
nothing else; do not hand-run `wslc exec ... /opt/...` from Git Bash, which rewrites Linux paths to
Windows ones before `wslc` sees them (`/opt/mssql-tools18/bin/sqlcmd` arrives as
`C:/Program Files/Git/opt/...` and the exec fails).

---

**Verification status** (run on 2026-09-10, wslc 2.9.11.0, WSL 2.9.11):

| Checked | Result |
|---|---|
| `up` from nothing — volume create, image pull, container create, wait for login | Ready in ~30s |
| Version parity with LocalDB | `17.0.4025.3` RTM, Enterprise Developer Edition — an exact match |
| Port binding | `127.0.0.1:14330->1433/tcp` only; the named volume `enlist-demo-sql-data` mounted at `/var/opt/mssql` |
| Control plane against it (Development, throwaway instance on :5299) | `MigrateAsync` created `EnlistControlPlane`, all five tables and `__EFMigrationsHistory` with the three migrations; `/api/agents` returned `[]` |
| LocalDB isolation | Unchanged throughout — the running demo on LocalDB never noticed |
| `down` then `up` | `down` shut the server down from the inside in 1s (`Server shut down by request from login sa`, `Exited (255)`); the schema and all three migrations survived the cycle; `up` took the start-existing path and was ready in 5s |
| `status`, `connstring` | Correct |
| `reset -Force` | **Not run.** The confirmation path was verified (it names the container, the volume and both blob directories), but the destructive branch was not executed, because doing so would have deleted real package blobs from this working tree |

Two things were found and fixed by that run. A `--health-cmd` string with embedded double quotes
was shredded by Windows PowerShell 5.1's native-argument quoting, and `wslc` then took a stray
token, `1`, as the image reference and matched it as an ID prefix of the *runner* image — so the
"SQL Server" container started enList's runner and exited with its usage text. The health check is
gone (the script probes a real login instead, which is the better signal anyway), and the script
keeps every native argument free of embedded quotes. And `wslc stop` proved to be a kill for this
image, which is why `down` shuts SQL Server down from the inside first. The script is ASCII-only
and BOM-prefixed, so Windows PowerShell 5.1 reads it correctly either way.
