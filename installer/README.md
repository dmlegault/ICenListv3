# enList installer — three MSIs and the bundle that chains them

`enList-3.0.0-Setup.exe` is the one thing a person runs. It chains two .NET runtimes and three Windows Installer packages:

| Package | Installs | Service |
|---|---|---|
| `Enlist.ControlPlane.msi` | `%ProgramFiles%\enList\ControlPlane` | `enlist-controlplane` |
| `Enlist.Portal.msi` | `%ProgramFiles%\enList\Portal` | `enlist-portal` |
| `Enlist.Agent.msi` | `%ProgramFiles%\enList\Agent`, plus `runner\` and `runner-legacy\` | `enlist-agent` |

They are designed page by page in [`Installer-UI-Design.md`](../docs/05-operations/Installer-UI-Design.md), which is the document to read first.

## Build and check

```powershell
.\build.ps1              # publish everything, build the MSIs, then the bundle
.\build.ps1 -SkipPublish # reuse publish\, for when only the WiX changed
.\verify.ps1             # 46 detection tests + 48 installer checks
.\verify.ps1 -Live       # really install, upgrade and uninstall (elevated shell)
```

## What the bundle decides

Which packages run is settled entirely by each package's install condition, comparing `INSTALLTYPE` against the three component switches:

```powershell
enList-3.0.0-Setup.exe /quiet INSTALLTYPE=AgentOnly AGENT_NAME=WEB-07 AGENT_CPURL=https://enlist.corp.local:5293
enList-3.0.0-Setup.exe /quiet INSTALLTYPE=Server DB_SERVER=sql01.corp.local
enList-3.0.0-Setup.exe /quiet INSTALLTYPE=Server InstallAgent=1     # and a local agent
enList-3.0.0-Setup.exe /quiet INSTALLTYPE=Custom InstallPortal=1    # the portal alone
```

`INSTALLTYPE` and the switches **add** rather than override, because Burn cannot derive one variable from a comparison of another. To leave a component out of a Server install, say `Custom` and name the ones you want.

**The .NET runtimes are downloaded, not carried.** The bundle holds a URL, a SHA-512 hash and a size for each; Burn fetches and verifies them at install time. An agent-only install skips ASP.NET Core entirely, which is 11 MB and a minute saved on every machine in a fleet. `tools\refresh-prerequisites.ps1` regenerates the hashes when the required .NET patch changes.

`build.ps1` reads the product version from `Directory.Build.props`, so there is no version to keep in step here.

## What these packages do, and deliberately do not

They lay down files, create the data directories, and install a service whose command line is assembled from properties. That is all. **No migrations, no HTTP calls, no enrollment, no secrets** — those belong to the bootstrapper that will chain these, which is why the design calls the MSIs dumb.

Three consequences worth knowing before wondering whether something is broken:

- **Every service is created stopped.** The control plane may have no schema yet, the portal no key, the agent no credential. A service that starts here and fails is a restart loop in the Event Log; one that waits is a clear state the bootstrapper resolves.
- **No secret ever reaches a service command line.** A `binPath` is readable by any local user out of the process list. So a SQL login produces no connection string at all (Windows authentication, which has no secret, does), the portal's own key is absent, and a join token is never passed. Each is the bootstrapper's job, and `verify.ps1` asserts all three absences.
- **`%ProgramData%` is never touched by an upgrade or an uninstall.** The package cache, staged runners and package blobs outlive both, on purpose.

## The two decisions taken here that are yours to overturn

**WiX 5, not 7.** WiX 7 is installed on this machine as a global tool and refuses to run without accepting the Open Source Maintenance Fee licence, which is a commercial commitment rather than a technical choice. WiX 5 is the last version under the old licence, it is pinned in `.config/dotnet-tools.json`, and the source here uses the v4+ schema that v5, v6 and v7 all share — so moving up later is a version bump and a licence decision, not a rewrite.

**This is not in `enList_v3.slnx`.** A WiX project in the solution would put an MSI build inside every `dotnet build` and every test run, for an artifact that changes far less often than the code. The cost is that `build.ps1` has to be run deliberately.

## Why `verify.ps1` is stranger than it looks

Most of what can go wrong with these packages cannot be seen by reading them. A service's `binPath` is a formatted string assembled from directory properties that have no final value until `CostFinalize`, and a mistake produces a service that installs perfectly and then fails at first start with nothing pointing at the cause.

So the offline half opens each package as a real Windows Installer session, sets the properties from the design's own silent-install examples, runs costing, evaluates each action's condition, and asserts on the command line that results. Two details in there were learned the hard way on 2026-09-13:

- **It parses every command line with `CommandLineToArgvW`**, the parser .NET actually uses, rather than matching on the string. That is what caught the defect where every directory argument ended in a backslash immediately before its closing quote: Windows reads `\"` as an escaped quote, so `--runner-bin "…\runner\"` swallowed the next flag whole and the agent would have started with a garbled path. Looking right and parsing right are different things.
- **It evaluates each action's condition before running it.** A custom action's condition lives in the sequence table, not in the action, so `Session.DoAction` runs it whether the condition holds or not. The first version of this script did exactly that and reported every conditional argument as present in every case — passing loudly while testing nothing.

## One correction to the design

Section 4 names the ASP.NET Core **Hosting Bundle** as the prerequisite for the control plane and portal. That is the right package for something hosted by IIS, and it is 117 MB against 11 MB, the difference being the IIS module. Nothing enList installs runs under IIS: the control plane and portal are Kestrel behind a Windows service, which is the only hosting the pages offer. The plain ASP.NET Core Runtime is used instead. If IIS hosting is ever offered, that is the package to revisit.

## The wizard, in `ba/`

Two projects, split on one line: what can be tested, and what cannot.

**`Enlist.Installer.Detection`** holds every decision. The registry shape the .NET installers actually write, the `wslc` and Docker probes, how `/health` is read, which wizard pages appear for which install type, when Next is allowed and why not, and what the collected settings become on the way to Burn. 46 tests cover it, several against this machine. It targets both `net472` (what a bootstrapper can load, since it runs before .NET 10 exists) and `netstandard2.0` (what the test project can reference).

**`Enlist.Installer.Ba`** is the WPF shell: the bootstrapper entry point, the engine conversation and the XAML. It holds no decisions, because a bootstrapper's pages cannot be exercised by a test.

**It is not wired into the bundle yet.** It builds and it launches, and three things about WiX 5's hosting contract were found the hard way getting that far, each of which fails while naming nothing useful:

- The bootstrapper is an **executable**. Burn does `CreateProcessW` on the primary payload, so pointing it at a DLL gives `ERROR_BAD_EXE_FORMAT` with nothing logged about a bootstrapper at all.
- It must match the bundle's **architecture**. A `net472` WPF project defaults to preferring 32-bit, which put an x86 assembly under an x64 engine and failed identically.
- Its entry point must **not** be `[STAThread]`. The host initialises COM on that thread first, so marking it STA fails with `RPC_E_CHANGED_MODE` and the process exits before a window can exist. The wizard runs on its own STA thread instead.

What it does not yet do is show its window: the bundle exits 0 without launching it. Running that down needs WiX's own hosting documentation rather than more inference, so the bundle uses the standard bootstrapper until then — which works, carries the whole of section 10's silent surface, and is what every check runs against. Whatever is in `out\` always installs.

## What is not here yet

Wiring the wizard in, and the pages beyond install type, prerequisites, agent and ready. Then the parts that need the bootstrapper to exist at all: minting a join token and exchanging it before the agent service is created, and minting the portal's key with `create-api-key` and storing it with `protect`.

Also outstanding: upgrade and repair verification across all three packages rather than the agent alone, and the open decisions in section 12, including code signing, which nothing here does.
