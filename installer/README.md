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
| `tools\` | **source** | `refresh-prerequisites.ps1`, which regenerates the download URLs, SHA-512 hashes and sizes in `Prerequisites.wxs` when the required .NET patch changes — run it on purpose, then commit what it wrote. `RunnerSourceHash.ps1` is the one definition of "what the runner image is built from", shared by `build.ps1` and `verify.ps1`. `probe-container-engines.ps1` (elevated) is a diagnostic, which asks Docker and `wslc` whether they answer the identities an installed agent can run as - LocalSystem, a user account in a non-interactive session, and SYSTEM after a reboot before anyone signs in. |
| `.config\` | **source** | `dotnet-tools.json`, which pins WiX 5. This is what makes the build reproducible rather than dependent on whatever `wix` happens to be on the machine. |
| `publish\` | *output* | `dotnet publish` of each component, one folder per component, including both runners. The input to harvesting. Each folder is emptied before its publish, because the packages harvest everything in it and `dotnet publish` never removes a file. |
| `staging\` | *output* | A copy of each publish folder **with the service executable removed**, rebuilt from scratch every run. It exists only because WiX 5's `Files` element has no exclude — see below. |
| `out\` | *output* | The three MSIs, the bundle, and their `.wixpdb` files. `enList-<version>-Setup.exe` here is the thing a person runs. Beside it, `enlist-runner-<version>.tar` and its `.sha256`: the runner image, handed out with the setup and loaded by the agent itself. |
| `.wix\` | *output* | The WiX extension cache, populated by `build.ps1` on first run. Delete it if an extension ever reports itself damaged. |

And three scripts at the root, which is the whole of the tooling: **`build.ps1`** publishes, harvests and builds; **`verify.ps1`** proves the packages without installing them (and with `-Live`, by installing them); **`live-e2e.ps1`** installs for real and then makes the product *run*, which is the only way to find the things none of the others can see.

**Why `staging\` exists.** MSI derives a service's binary path from the key path of the component its `ServiceInstall` sits in, so each service executable has to be declared by hand in the `.wxs` rather than harvested with everything else. WiX 5 has no way to exclude one file from a `Files` glob, so `build.ps1` harvests a copy that does not contain it. The `.wxs` then declares that one file explicitly. Each package therefore reads from both trees: `$(…Staging)` for the glob and `$(…Publish)` for the service exe.

## Build and check

```powershell
.\build.ps1              # publish everything, build the runner image, the MSIs, then the bundle
.\build.ps1 -SkipPublish # reuse publish\, for when only the WiX changed
.\build.ps1 -SkipRunnerImage  # no Docker here: build the MSIs without the runner image
.\verify.ps1             # 155 detection tests + 103 installer checks
.\verify.ps1 -Live       # really install, upgrade and uninstall (elevated shell)
.\live-e2e.ps1           # a real install that RUNS (elevated shell) - see below
```

**`live-e2e.ps1` is the one that cannot be faked.** `verify.ps1 -Live` proves the packages install, upgrade and uninstall; this proves the product *works* — a silent Server install over TLS through the bundle, the schema applied, the portal's key minted and stored, both services started and answering HTTPS, a real join token exchanged for an agent credential, and then **a real application deployed to that installed agent**: the sample package uploaded with `enlist-deploy`, placed on the agent by a rule, and run twice — as a process (the staged runner, a copy of the installed one, running as SYSTEM) and in a container — then taken away again and confirmed stopped. After that, all of it is removed. The container runs through **`wslc`** by default, as the wizard defaults, and nobody loads the image: the script removes it from SYSTEM's store and deletes the previous run's `Images` folder first, the installer copies the download from beside the setup in `out\` into the agent's `Images` folder, and the checks are that the copy is there with its locked-down ACL, that the *agent* loaded it (`loaded.json`, its log lines from this run only, SYSTEM's `wslc image inspect`), and that the container runs from it. `wslc` is asked as SYSTEM, through a scheduled task, because its image store is per account. `-ContainerEngine docker` proves the same through Docker. **Every check passed through `wslc` (2026-09-14, 18:28) and through Docker (13:57)** — the `wslc` run on a machine that had had the agent before, which is the reinstall path. It needs the repository built (`dotnet build enList_v3.slnx`, for the sample and the deploy tool) and the engine working; `-SkipContainers` runs the process half alone and says so. It takes a thumbprint and a transcript path:

```powershell
.\live-e2e.ps1 -Thumbprint <cert in LocalMachine\My> -Transcript "$env:TEMP\enlist-e2e.txt"
```

It always uninstalls, in a `finally`. `-TrustCertificate` adds a self-signed test certificate to `LocalMachine\Root` for the run and removes it again, which is what an agent needs before it will talk to a control plane serving one — the agent runs as LocalSystem and consults the *machine* trust store, not the operator's.

**The runner image is part of the build, and a separate download.** The agent package tells every container-mode agent to run `enlist/runner:<product version>`, so `build.ps1` builds that image with Docker, labelled with the version and a SHA-256 of the source it came from, and saves it into `out\` as `enlist-runner-<version>.tar` with a `.sha256` beside it. It is deliberately **not** inside the installer (Installer-UI-Design section 12 item 5), and nobody has to load it either - **the agent loads it, as its own account**, from its `Images` folder (`C:\ProgramData\enList\Agent\Images`). That is what makes `wslc` usable for a service: `wslc` keeps an image store per account, so an image loaded in an operator's shell is invisible to a LocalSystem agent. Hand the files out beside the setup executable and the installer copies them into that folder; or copy them there later, and the agent loads them before its next container start. It loads only `enlist/runner:*` archives, checks the `.sha256`, and loads each file once (`loaded.json`). `Enlist.Agent.msi` creates the folder protected - SYSTEM and Administrators only - because whoever can write to it chooses what LocalSystem loads, and re-applies that on every install, reinstall and upgrade included; the bootstrapper never creates it.

Beside them, `enlist-runner-<version>-README.md` says where the file goes, with the `docker load -i` / `wslc load -i` commands for loading it by hand, so the instructions travel with the file. It is a plain tar, about 80 MB — `docker save` already compresses the layers, and gzip saved half a megabyte. The wizard says it too: choosing a container engine on the Agent page shows, in place of the "leave it empty" hint, whether the download was found beside the setup, and the Finish page repeats it. Agents never pull, so until the file is in place a container-mode agent reports the image missing by name, and says where to put it. Docker not running stops the build with a message rather than skipping quietly; `-SkipRunnerImage` is the deliberate way past it, and removes any earlier download from `out\` so a stale one cannot be handed out.

### What to hand out

Everything a recipient needs is in `out\`, and it is two things, not one:

| Give to | Files | Notes |
|---|---|---|
| everyone installing enList | `enList-<version>-Setup.exe` | The only installer. The three MSIs are embedded in it; the `.msi` and `.wixpdb` files beside it are build by-products. The .NET runtimes are **downloaded** at install time if missing, so a machine with no internet access needs them installed first. |
| machines that run applications in containers | `enlist-runner-<version>.tar`, `.tar.sha256`, `enlist-runner-<version>-README.md` | The runner image. Beside the setup, the installer copies it into the agent's `Images` folder and the agent loads it; otherwise copy it there, as the README says. Not needed by agents that run applications as processes, or by the control plane and portal. |

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

They lay down files, create the data directories, and install a service whose command line is assembled from properties. That is all. **No migrations, no HTTP calls, no enrollment, no secrets** — those belong to the bootstrapper that chains them (its post-install steps are in [`ba\README.md`](ba/README.md)), which is why the design calls the MSIs dumb.

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

Two more groups check what the packages *contain* rather than what they do, and both read the built artifacts rather than the source, because the source is not what ships:

- **Every installer and every enList executable carries the iC icon** — the bundle, the wizard, each MSI, and the three services and both runners as they sit inside their MSIs. Each executable is read out of its package's embedded cabinet through `msi.dll` and compared with `enlist.ico` byte for byte, with an unbranded executable as a control. Comparing rendered pixels, the first attempt, could not tell the generic icon from the real one.
- **The runner image download is there, and is the image the agent is told to use.** The name is read out of `Enlist.Agent.msi`; then `out\enlist-runner-<version>.tar` has to load under exactly that tag, carry a matching version label and a source hash equal to the runner source as it is now, and match its `.sha256`. All read from the file with `tar.exe`, so no Docker is needed. By content, not by time: Docker's cache keeps an unchanged image's creation date, so "built after the newest file" fails a current image the moment a file is merely touched. The README beside it has to name the agent's `Images` folder and both load commands.
- **The agent's Images folder is locked down.** The agent loads, as LocalSystem, whatever runner image is put in `Images\`, so the folder must not inherit `%ProgramData%`'s "Users may create files". Read out of `Enlist.Agent.msi`: the directory sits under the agent's data folder, and its `MsiLockPermissionsEx` SDDL is protected and names SYSTEM and Administrators, and no Users, Authenticated Users, Everyone or Interactive. And no component that creates it is Permanent or NeverOverwrite: the lock first lived in the data folder's component, whose registry key path outlives an uninstall, so every reinstall and upgrade skipped it and the folder inherited ProgramData's ACL. Only a real install on a machine that had the agent before could show that - the first `live-e2e.ps1` run through `wslc` did.
- **Nothing design-time ships.** The control plane's `Microsoft.EntityFrameworkCore.Design` reference exists for `dotnet ef`, and Roslyn's build host — `Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.exe` and about 4 MB beside it — used to reach the control plane MSI as NuGet content files. The project now leaves it out of publish, `build.ps1` empties each publish folder so a stale copy cannot linger, and this check reads every package's File table to hold it that way.

One defect it found is worth knowing about before editing any `.wxs` here: **a bundle variable that is empty replaces an MSI's default rather than falling back to it.** A `Property` element's value is the Property-table default, and the bundle passes every variable to every package unconditionally. `DB_SERVER` empty produced `Server=;` in a connection string — a control plane that installs perfectly and never starts. Every default in the three packages is therefore a conditional `SetProperty`, not a `Property` value, and there is a group of checks that holds it that way.

## One correction to the design

Section 4 names the ASP.NET Core **Hosting Bundle** as the prerequisite for the control plane and portal. That is the right package for something hosted by IIS, and it is 117 MB against 11 MB, the difference being the IIS module. Nothing enList installs runs under IIS: the control plane and portal are Kestrel behind a Windows service, which is the only hosting the pages offer. The plain ASP.NET Core Runtime is used instead. If IIS hosting is ever offered, that is the package to revisit.

## The wizard, in `ba\`

Three projects, split on one line: what can be tested, and what cannot. `Enlist.Installer.Detection` holds every decision and has 155 tests over it; `Enlist.Installer.Ba` is the WPF shell and holds none, because a bootstrapper's pages cannot be exercised by a test.

**It is what the bundle chains.** Eight pages, styled to the portal's own palette, with `build.ps1 -StandardBootstrapper` as the way back to the stock WixStdBA if it ever regresses. A silent install reaches no window under either one, so the surface in section 10 is unaffected by the choice.

[`ba\README.md`](ba/README.md) has the rest: how Burn runs a bootstrapper out of process, the engine callbacks, and the three stacked defects that each surfaced as nothing but a closed pipe.

## What is not here yet

Nothing blocking an install. `live-e2e.ps1` passes end to end: schema, portal key, TLS, agent enrollment, and uninstall.

**Which container engine, and why `wslc`** (section 12 item 11). The wizard defaults to `wslc`, then Docker: through a reboot, `tools\probe-container-engines.ps1 -ArmStartupProbe` had `wslc` answering SYSTEM, loading and running the runner image at every sample, while Docker Desktop - an application in a user's session - never started. `wslc`'s per-account image store, which briefly made Docker the default, is answered by the agent loading the image from its `Images` folder as itself, and `live-e2e.ps1` proves that path by default. Its first run there failed only on that folder's ACL, which reinstalls had been skipping - fixed, and now held by `verify.ps1`.

Still outstanding:
- `verify.ps1 -Live` proves upgrade and uninstall for the agent package only, not all three.
- Nothing is code-signed (section 12 item 7, which needs a certificate purchase rather than a design).
- `live-e2e.ps1` has only ever installed the agent as LocalSystem. A named agent account - which gets its own grant on the `Images` folder - has not been run for real.
- The post-install steps assume the bootstrapper is elevated: they write into `%ProgramFiles%` and into the Administrators-only `Images` folder. Every live run starts setup from an elevated shell; a double-clicked, unelevated setup has not been tried.
- `%ProgramData%\enList\Agent` itself still inherits `%ProgramData%`'s ACL, so an ordinary user can create files under it - `Runners\` included, where the agent stages executables. Only `Images\` is locked down so far. The fix is planned as phase P0 of [Program-Workloads-Design.md](../docs/03-architecture/Program-Workloads-Design.md) §16: `Packages\`, `Runners\` and `AppData\` each get the Images-folder treatment - their own component, applied on every install, with a grant for a named agent account.
- Agent shutdown stops applications one at a time, giving each up to 10 seconds, and `Program.cs` never sets the generic host's `ShutdownTimeout`, so .NET's default applies. With several slow applications, `sc stop` or an MSI upgrade (`ServiceControl Wait="yes"`) can end runners mid-stop. Found 2026-09-15 while designing program workloads; planned as P0 of the same design (§9 item 8: parallel stops, an explicit host timeout, additional time from the SCM).
