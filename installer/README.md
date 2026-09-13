# enList installer — the three MSIs

Three Windows Installer packages, built from `src/*.wxs`:

| Package | Installs | Service |
|---|---|---|
| `Enlist.ControlPlane.msi` | `%ProgramFiles%\enList\ControlPlane` | `enlist-controlplane` |
| `Enlist.Portal.msi` | `%ProgramFiles%\enList\Portal` | `enlist-portal` |
| `Enlist.Agent.msi` | `%ProgramFiles%\enList\Agent`, plus `runner\` and `runner-legacy\` | `enlist-agent` |

They are designed page by page in [`Installer-UI-Design.md`](../docs/05-operations/Installer-UI-Design.md), which is the document to read first. This directory is the second of the seven steps that document implies: the packages themselves, with no user interface at all.

## Build and check

```powershell
.\build.ps1              # publish everything, then build the three MSIs
.\build.ps1 -SkipPublish # reuse publish\, for when only the WiX changed
.\verify.ps1             # 28 checks, no administrator rights needed
.\verify.ps1 -Live       # really install, upgrade and uninstall (elevated shell)
```

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

## What is not here yet

Steps four through seven of the plan: the Burn bundle that chains these three plus the .NET 10 hosting bundle, the managed bootstrapper that collects the properties, the pages that mint a join token and the portal's key, and the upgrade and repair verification across all three packages rather than just the agent. Section 12 of the design also lists the open decisions, including code signing, which nothing here does.
