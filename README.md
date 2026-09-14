# enList v3

Run small programs — long-running **services** and scheduled **jobs** — across many Windows machines,
controlled from a single web portal. Upload a package, write a rule saying *where* it should run,
and an agent on each machine makes it so. The same package can run as an ordinary process on one
machine and inside a container on another, with nothing rebuilt.

**New here? → [`docs/README.md`](docs/README.md)** is the map. It opens with a four-document,
roughly two-hour path from "never seen this" to "I can find my way around the code".

If you have five minutes and want the plain-language version first, read
[`docs/01-start-here/One-Page-Overview.md`](docs/01-start-here/One-Page-Overview.md).

## Run it locally

Full instructions, including installing an agent and deploying a sample application, are in
[`docs/01-start-here/Developer-Setup-Guide.md`](docs/01-start-here/Developer-Setup-Guide.md). The
short version:

```bash
dotnet build enList_v3.slnx
```

```bash
dotnet run --project src/Enlist.ControlPlane/Enlist.ControlPlane.csproj --urls http://localhost:5293
```

Requires the .NET 10 SDK and SQL Server or LocalDB. On first run in `Development`, EF Core
migrations create the `EnlistControlPlane` database automatically.

## Repository layout

| Path | What it is |
|---|---|
| `src/` | The five components — `Enlist.Portal` (Blazor Server UI), `Enlist.ControlPlane` (REST + SignalR + database), `Enlist.Agent` (one per machine), `Enlist.Runner` (hosts one application; `.Legacy` is the net472 build), `Enlist.Deploy` (the publish CLI), plus `Enlist.ControlPlane.Contracts`. `src/branding/` holds the iC icon the executables carry |
| `installer/` | The Windows installer: three MSIs, a Burn bundle and its WPF wizard, and the scripts that build and prove them. Not in the solution — **start at [`installer/README.md`](installer/README.md)** |
| `contracts/` | `EnlistAttributes.cs` — the attributes an application marks its services and jobs with. Source-only, imported via `.props`, deliberately not a package |
| `samples/` | Reference applications: a service, a job-based order processor, a data pipeline, and a net472 legacy sample |
| `deploy/` | Built sample payloads used by the tests and the setup walkthrough |
| `tests/` | Five test projects (Agent, ControlPlane, Deploy, Portal, Runner), a deliberately misbehaving test plugin, and `Enlist.TestSupport`, which spawns real control-plane, agent and runner processes rather than mocking them |
| `docs/` | The documentation set — **start at [`docs/README.md`](docs/README.md)** |
| `demo/sql-server/` | SQL Server in a WSL container (`wslc`, no Docker) for demos, so demo data persists and is visible in SSMS. Development and the test suite use LocalDB and need none of it |
| `demo/start-demo.ps1` | Starts or restarts the whole demo: the demo SQL Server, the control plane on :5293 pointed at it, and both demo agents. `-Stop` stops the processes |

## Tests

```bash
dotnet test enList_v3.slnx
```

The suite spawns real processes and a real database rather than mocking them, so it needs LocalDB
reachable and takes a little longer than a pure unit-test suite. See
[`docs/05-operations/Test-Plan.md`](docs/05-operations/Test-Plan.md) for what it covers and what it
does not yet.

Some tests skip rather than fail when the machine cannot run them, and a skip is worth reading:

- **The agent's container tests** need Docker running *and* a current runner image. The image is
  built from source and nothing rebuilds it for you, so after any change under `src/Enlist.Runner`
  rebuild it before trusting a green run — otherwise the tests exercise yesterday's runner:

  ```bash
  docker build -f src/Enlist.Runner/Dockerfile -t enlist/runner:dev .
  ```

- **Three portal sign-in tests** need an account that can start a Windows sign-in over the network:
  a domain member, or an account with a network-usable password.

The installer is tested separately, by `installer/verify.ps1` — see its README.
