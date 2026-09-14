# enList installer — three MSIs and the bundle that chains them

`enList-3.0.0-Setup.exe` is the one thing a person runs. It chains two .NET runtimes and three Windows Installer packages:

| Package | Installs | Service |
|---|---|---|
| `Enlist.ControlPlane.msi` | `%ProgramFiles%\enList\ControlPlane` | `enlist-controlplane` |
| `Enlist.Portal.msi` | `%ProgramFiles%\enList\Portal` | `enlist-portal` |
| `Enlist.Agent.msi` | `%ProgramFiles%\enList\Agent`, plus `runner\` and `runner-legacy\` | `enlist-agent` |

They are designed page by page in [`Installer-UI-Design.md`](../docs/05-operations/Installer-UI-Design.md), which is the document to read first.

## Layout

Four directories are source and four are build output. Nothing under the second group is in git, and all of it can be deleted at any time — `build.ps1` recreates it.

| | | |
|---|---|---|
| `src\` | **source** | The WiX. Three `.wxs` packages, `Bundle.wxs` for the Burn bundle, `Prerequisites.wxs` for the two .NET runtimes it chains, and `Common.wxi` for what all of them share (manufacturer, the three service names). |
| `ba\` | **source** | The bootstrapper application: the wizard, the detection library behind it, and that library's tests. Three projects and a hosting contract with sharp edges — [`ba\README.md`](ba/README.md) covers it. `build.ps1` builds it first and hands the bundle its output folder. |
| `tools\` | **source** | `refresh-prerequisites.ps1`, which regenerates the download URLs, SHA-512 hashes and sizes in `Prerequisites.wxs` when the required .NET patch changes. Run it on purpose, then commit what it wrote. |
| `.config\` | **source** | `dotnet-tools.json`, which pins WiX 5. This is what makes the build reproducible rather than dependent on whatever `wix` happens to be on the machine. |
| `publish\` | *output* | `dotnet publish` of each component, one folder per component, including both runners. The input to harvesting. |
| `staging\` | *output* | A copy of each publish folder **with the service executable removed**, rebuilt from scratch every run. It exists only because WiX 5's `Files` element has no exclude — see below. |
| `out\` | *output* | The three MSIs, the bundle, and their `.wixpdb` files. `enList-<version>-Setup.exe` here is the thing a person runs. |
| `.wix\` | *output* | The WiX extension cache, populated by `build.ps1` on first run. Delete it if an extension ever reports itself damaged. |

And three scripts at the root, which is the whole of the tooling: **`build.ps1`** publishes, harvests and builds; **`verify.ps1`** proves the packages without installing them (and with `-Live`, by installing them); **`live-e2e.ps1`** installs for real and then makes the product *run*, which is the only way to find the things none of the others can see.

**Why `staging\` exists.** MSI derives a service's binary path from the key path of the component its `ServiceInstall` sits in, so each service executable has to be declared by hand in the `.wxs` rather than harvested with everything else. WiX 5 has no way to exclude one file from a `Files` glob, so `build.ps1` harvests a copy that does not contain it. The `.wxs` then declares that one file explicitly. Each package therefore reads from both trees: `$(…Staging)` for the glob and `$(…Publish)` for the service exe.

## Build and check

```powershell
.\build.ps1              # publish everything, build the MSIs, then the bundle
.\build.ps1 -SkipPublish # reuse publish\, for when only the WiX changed
.\verify.ps1             # 138 detection tests + 84 installer checks
.\verify.ps1 -Live       # really install, upgrade and uninstall (elevated shell)
.\live-e2e.ps1           # a real install that RUNS (elevated shell) - see below
```

**`live-e2e.ps1` is the one that cannot be faked.** `verify.ps1 -Live` proves the packages install, upgrade and uninstall; this proves the product *works* — a silent Server install over TLS through the bundle, the schema applied, the portal's key minted and stored, both services started and answering HTTPS, a real join token exchanged for an agent credential, and then all of it removed. It takes a thumbprint and a transcript path:

```powershell
.\live-e2e.ps1 -Thumbprint <cert in LocalMachine\My> -Transcript "$env:TEMP\enlist-e2e.txt"
```

It always uninstalls, in a `finally`. `-TrustCertificate` adds a self-signed test certificate to `LocalMachine\Root` for the run and removes it again, which is what an agent needs before it will talk to a control plane serving one — the agent runs as LocalSystem and consults the *machine* trust store, not the operator's.

`build.ps1` also takes `-Version` and `-OutputDirectory`, which exist for one purpose: building a higher version of the same source, somewhere else, so an upgrade can be tested without replacing the real output. `verify.ps1 -Live` uses both.

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
- **There is no default SQL Server, and LocalDB is refused.** A LocalDB instance belongs to the account that starts it, so the database this installer creates as the elevated operator is *not* the one a service can see — it gets an empty instance of its own, and the control plane correctly refuses to create a schema outside Development. The operator is then left with "Cannot connect to the control plane database" after an install that reported success. With no `DB_SERVER` this package writes no connection string at all, the control plane refuses by name (`DatabaseRules`), and the wizard blocks the page. Found by installing for real.

## The two decisions taken here that are yours to overturn

**WiX 5, not 7.** WiX 7 is installed on this machine as a global tool and refuses to run without accepting the Open Source Maintenance Fee licence, which is a commercial commitment rather than a technical choice. WiX 5 is the last version under the old licence, it is pinned in `.config/dotnet-tools.json`, and the source here uses the v4+ schema that v5, v6 and v7 all share — so moving up later is a version bump and a licence decision, not a rewrite.

**This is not in `enList_v3.slnx`.** A WiX project in the solution would put an MSI build inside every `dotnet build` and every test run, for an artifact that changes far less often than the code. The cost is that `build.ps1` has to be run deliberately.

## Why `verify.ps1` is stranger than it looks

Most of what can go wrong with these packages cannot be seen by reading them. A service's `binPath` is a formatted string assembled from directory properties that have no final value until `CostFinalize`, and a mistake produces a service that installs perfectly and then fails at first start with nothing pointing at the cause.

So the offline half opens each package as a real Windows Installer session, sets the properties from the design's own silent-install examples, runs costing, evaluates each action's condition, and asserts on the command line that results. Two details in there were learned the hard way on 2026-09-13:

- **It parses every command line with `CommandLineToArgvW`**, the parser .NET actually uses, rather than matching on the string. That is what caught the defect where every directory argument ended in a backslash immediately before its closing quote: Windows reads `\"` as an escaped quote, so `--runner-bin "…\runner\"` swallowed the next flag whole and the agent would have started with a garbled path. Looking right and parsing right are different things.
- **It evaluates each action's condition before running it.** A custom action's condition lives in the sequence table, not in the action, so `Session.DoAction` runs it whether the condition holds or not. The first version of this script did exactly that and reported every conditional argument as present in every case — passing loudly while testing nothing. Fixing it failed three checks immediately, which is how the empty-variable defect below was found.

`-Live` is the other half, and it drives the **bundle** rather than `msiexec`, because the bundle is what an operator runs and it is the layer that passes the variables. It installs, upgrades to a higher version built from the same source, and uninstalls, asserting at each step: the service's account, start type and parsed command line; that an upgrade leaves exactly one registration and does not touch `%ProgramData%`; and that an uninstall removes the service and the binaries while correctly leaving `%ProgramData%` and the shared .NET runtime behind. It needs an elevated shell and it really does install — currently the agent only.

One defect it found is worth knowing about before editing any `.wxs` here: **a bundle variable that is empty replaces an MSI's default rather than falling back to it.** A `Property` element's value is the Property-table default, and the bundle passes every variable to every package unconditionally. `DB_SERVER` empty produced `Server=;` in a connection string — a control plane that installs perfectly and never starts. Every default in the three packages is therefore a conditional `SetProperty`, not a `Property` value, and there is a group of checks that holds it that way.

## One correction to the design

Section 4 names the ASP.NET Core **Hosting Bundle** as the prerequisite for the control plane and portal. That is the right package for something hosted by IIS, and it is 117 MB against 11 MB, the difference being the IIS module. Nothing enList installs runs under IIS: the control plane and portal are Kestrel behind a Windows service, which is the only hosting the pages offer. The plain ASP.NET Core Runtime is used instead. If IIS hosting is ever offered, that is the package to revisit.

## The wizard, in `ba\`

Three projects, split on one line: what can be tested, and what cannot. `Enlist.Installer.Detection` holds every decision and has 138 tests over it; `Enlist.Installer.Ba` is the WPF shell and holds none, because a bootstrapper's pages cannot be exercised by a test.

**It is what the bundle chains.** Eight pages, styled to the portal's own palette, with `build.ps1 -StandardBootstrapper` as the way back to the stock WixStdBA if it ever regresses. A silent install reaches no window under either one, so the surface in section 10 is unaffected by the choice.

[`ba\README.md`](ba/README.md) has the rest: how Burn runs a bootstrapper out of process, the engine callbacks, and the three stacked defects that each surfaced as nothing but a closed pipe.

## What is not here yet

The parts that need the bootstrapper to exist at all, now that one does: minting a join token and exchanging it before the agent service is created, and minting the portal's key with `create-api-key` and storing it with `protect`. Until those land, the wizard collects a join token and a portal password but the install does not yet act on them.

Also outstanding: upgrade and repair verification across all three packages rather than the agent alone, and the open decisions in section 12, including code signing, which nothing here does.
