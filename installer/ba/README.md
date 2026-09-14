# `ba/` — the bootstrapper application

Three projects. The split between them is one line: **what can be tested, and what cannot.**

| Project | Target | What it is |
|---|---|---|
| `Enlist.Installer.Detection` | `netstandard2.0` + `net472` | Every decision the installer makes. No UI, no engine. |
| `Enlist.Installer.Detection.Tests` | `net10.0` | 157 cases over the above, several against this machine. |
| `Enlist.Installer.Ba` | `net472` WPF, `WinExe` | The wizard Burn runs. Binding and navigation only. |

A Burn bootstrapper cannot be exercised by a test — it is a process started by a native host inside an elevated install. So anything with a judgement in it lives in the detection library, where a test can reach it, and the wizard is left holding as close to nothing as it can be.

## How Burn actually runs this

Worth having straight before reading any of it, because most of the hosting contract is invisible in the source and every part of it fails while naming something else.

```
enList-3.0.0-Setup.exe            the bundle - a Burn engine with payloads attached
   |
   |  extracts the BA payload to a temp folder, then CreateProcessW
   v
Enlist.Installer.Ba.exe           a SEPARATE PROCESS, talking back over a pipe
   |
   |  ManagedBootstrapperApplication.Run(new EnlistBootstrapperApplication())
   |    reads the pipe name and API version off its own command line,
   |    connects back, and pumps the engine's callbacks
   v
   Detect  ->  Plan  ->  Apply
```

**Out of process, and an executable.** WiX 5 does not load a BA as a DLL. Burn `CreateProcessW`s the primary payload, so pointing the bundle at a DLL gives `ERROR_BAD_EXE_FORMAT` with *nothing in the log about a bootstrapper at all*. `mbanative.dll` is a helper this process loads, not the entry point.

**AnyCPU with `Prefer32Bit` off.** A `net472` WPF project prefers 32-bit by default. An x86 assembly under an x64 engine fails identically to the above, which is a confusing way to learn about a platform target.

**`Main` must not be `[STAThread]`** — the opposite of what a WPF entry point usually wants. The host has already initialised COM on that thread, so marking it STA fails with `RPC_E_CHANGED_MODE` and the process exits before a window can exist.

**But `Run()` is already on an STA thread, and must not start another.** The base class's `OnStartup` does this and returns:

```csharp
Thread uiThread = new Thread(this.Run);
uiThread.SetApartmentState(ApartmentState.STA);
uiThread.Start();
```

So `Run()` builds the window, captures `Dispatcher.CurrentDispatcher`, calls `Dispatcher.Run()`, and every engine callback marshals back onto that.

**`net472`, and not by preference.** The BA runs *before* the prerequisites it exists to install. It cannot need .NET 10 — it would be asking the machine for the thing it is there to provide. .NET Framework 4.7.2 is inbox on every supported Windows, so it is the one runtime that can be assumed.

### The engine conversation

`EnlistBootstrapperApplication` is the whole of it. Burn calls on its own thread and expects prompt answers:

| Callback | What happens |
|---|---|
| `OnCreate` | Keeps the `IBootstrapperCommand` — this is where `Display` and `Action` arrive. |
| `Run` | Silent: `Detect()` and wait. Interactive: start the UI thread, wait for the window, then `Detect()`. |
| `OnDetectComplete` | Hands to the wizard, or — silent — goes straight to `Plan(command.Action)`. |
| `OnPlanComplete` | `Apply()` on success, finish on failure. |
| `OnCacheAcquireProgress` | Download progress. Most of the wait on a machine without .NET is here; a bar that only moves during execution looks stuck for all of it. |
| `OnExecuteProgress` | Install progress. |
| `OnExecutePackageBegin` / `OnExecutePackageComplete` | Records which packages this apply actually executed. The post-install steps key on that, not on the plan — see "After the packages" below. |
| `OnApplyComplete` | Runs the post-install steps on a successful install, reports the result to the wizard, then finishes. |
| `OnError` | Shows the message and returns `Result.Ok` — **never retries silently.** An error the operator cannot see, recovered from in a way they did not choose, is how an install ends up in a state nobody can explain. |

**A silent install never reaches the window.** `Display.Full` and `Display.Passive` get a wizard; everything else plans and applies straight through. That is what makes the silent surface in Installer-UI-Design section 10 behave identically with or without a wizard — the window is one way of filling in variables, not a second way of installing.

`Program.Main` wraps the lot in a `try` and appends anything that escapes to `%LocalAppData%\Temp\enlist-bootstrapper-crash.log`. A bootstrapper that dies silently leaves an operator with a setup window that never appeared and a log saying only that a process exited.

## `Enlist.Installer.Detection` — where the decisions live

Four things it finds out about a machine, two it proves about what was typed, and one it computes.

**`RuntimeDetection`** reads the registry shape the .NET installers actually write: one DWORD per version under `SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\<framework>`, with no "latest" to read. `InstalledVersions` enumerates and `Check` compares properly against a minimum — precisely the thing a Burn `RegistrySearch` cannot do, since it can only ask whether one exact value name exists.

**That is not just for the page any more.** `PublishRuntimeDetection` in the BA sets the `AspNetCorePresent` bundle variable from this, before `Detect`, and the ASP.NET Core package's `DetectCondition` reads it. Without that, a machine with 10.0.11 failed the bundle's check for 10.0.12 and downloaded 11 MB it already had a newer copy of. `ParseMinimum` takes the minimum as it arrives from a Burn variable and falls back rather than throwing — this runs before any window exists, so an exception would reach the operator as nothing but a closed pipe.

`CheckNetFramework472` covers the legacy runner's requirement.

**`ContainerEngineDetection`** probes `wslc` and `docker` with a 10 second timeout. Neither is required, and none is a perfectly good answer. **`DefaultEngine` proposes Docker when it answers and otherwise nothing — never `wslc`.** It used to be "the first one found", `wslc` first. But detection runs as the operator and the agent runs as a LocalSystem service, and `wslc` keeps a separate image store for every account: the runner image an operator loads is not in LocalSystem's store, so the agent cannot find it (`tools\probe-container-engines.ps1 -Wslc`). Docker's store is shared by every account. `InstallPlan.AgentEngineWarning` is the other half: `wslc` typed in for a service account gets a red note on the Agent and Finish pages saying to load the image as that account, or use Docker. (For a few hours that note said `wslc` *hangs* as a service, from one probe that saw three silent minutes; every later run answered at once.)

**`Probes`** is the two things that need a network. `VerifyControlPlaneAsync` reads `/health` — it takes an optional `HttpMessageHandler` purely so tests can hand it one. `TestDatabaseAsync` opens a connection and reports whether it opened. `BuildConnectionString` assembles what both sides use.

> On `net472` the SQL client is **inbox**, in `System.Data.dll`. That matters more than it looks: `Microsoft.Data.SqlClient`, the obvious choice, drags `Azure.Identity` and a dozen other assemblies into a payload Burn extracts before anything is installed. The bootstrapper tests one connection.

**`InstallPlan`** is everything the operator chose, and three methods over it:

- `Pages()` — which of `InstallType, Prerequisites, ControlPlane, Database, Portal, Agent, Ready` this plan shows. Welcome, License, Installing and Finished never vary.
- `WhyNextIsBlocked(page)` — the reason, naming the field, or `null`. One at a time. A greyed-out button with no explanation is the worst thing a wizard page can do, so the reason is bound beside the button.
- `ToBundleVariables()` — what the plan becomes on the way to Burn. **Only what was actually set**: an empty value would override the bundle's own default with nothing, and for a listen URL or a service account that is the difference between a working service and one that will not start. The same defect arriving from the other direction is what the conditional `SetProperty` defaults in the three `.wxs` files defend against.

`AgentJoinToken` is on the plan and deliberately *not* in that dictionary. A token on a command line is readable out of the process list.

### Two targets, both needed

`net472` is what the bootstrapper loads. `netstandard2.0` is what the `net10.0` test project can reference. The code is identical; only the consumers differ, and testing either tests both.

## `Enlist.Installer.Detection.Tests`

```powershell
cd installer
.\verify.ps1
```

104 methods, 157 cases. `InstallPlanTests` is the bulk of it — page flow, blocking reasons, and the variable dictionary. `ProbeTests` uses a stub `HttpMessageHandler` for `/health` and `SkippableFact` for anything needing a real SQL Server. `RuntimeDetectionTests` and `ContainerEngineDetectionTests` run against this machine and skip rather than fail where it cannot answer.

Not in `enList_v3.slnx`, for the same reason the rest of `installer\` is not: it belongs to an artifact built deliberately, not on every inner loop.

## `Enlist.Installer.Ba` — the wizard

`WizardViewModel` is the only file of any size, and it holds no decisions: it asks `InstallPlan` which pages exist, asks `WhyNextIsBlocked` whether Next is allowed, and calls the detection library for the Prerequisites grid and the two Verify buttons. `WizardWindow.xaml` is one window with every page in it, switched by visibility, with `Installing` and `Finished` as borders over the top.

The Prerequisites grid marks the two .NET runtimes **non-blocking** on purpose — the bundle installs whichever is missing, which is what a bundle is for. It reports them so the operator knows what the wait will be.

## The state of it: wired in and working

`src/Bundle.wxs` chains this wizard. `build.ps1 -StandardBootstrapper` puts the stock WixStdBA back, which is the fallback if it ever regresses — the silent surface is identical either way, because a silent install reaches no window under either one.

It took three defects stacked on each other to get here, and they are worth knowing because **every one of them produced the same symptom**: the engine reporting `0x800700e8`, "the pipe is being closed". That is all Burn can ever say when a bootstrapper process dies, so each had to be dug out before the next was visible.

1. **`mbanative.dll` was not in the build output.** A `net472` project copies nothing out of `runtimes\`. `DllNotFoundException` on the first call into the engine. The csproj copies it by hand now, and `build.ps1` refuses to build a bundle missing any of the four payloads.
2. **`<ProgressBar Value="{Binding Progress}">`.** `RangeBase.Value` is `BindsTwoWayByDefault`, `Progress` has a private setter, and WPF threw `InvalidOperationException` straight out of `Window.Show()`.
3. **A `DataTemplate` that contained itself** — see the note in the XAML. `StackOverflowException` inside WindowsBase, which .NET cannot catch and no handler can log. The process simply vanished.

The threading was also backwards, though that was wasted effort rather than a defect: `Run()` started its own STA thread on the theory that the calling thread could not be made one. True of `Main`, not of `Run()`.

**Diagnostics exist now because two of those three could not report themselves.** Lifecycle milestones go into the engine's own log — `window shown`, `OnDetectComplete`, `pump ended`, `Run returning` — so a dead bootstrapper leaves a position rather than a closed pipe. `Program.LogCrash` is shared and hooked to `AppDomain.UnhandledException`, because the `catch` in `Main` only ever covered `Main`'s thread and the wizard does not run there.

## Branding

The palette is the portal's, not a second opinion about what enList looks like: the values come from `MudTheme` in `src\Enlist.Portal\Components\Layout\MainLayout.razor`, which runs dark, and the 12px radius is that theme's `DefaultBorderRadius`. Restyling the portal should mean changing these too — they are copied rather than shared because a WPF bootstrapper running before .NET 10 exists cannot reference anything the portal uses.

The Iron Canary mark is the banner and, as a generated multi-size `.ico`, the window icon. Both are compiled in as WPF `Resource`s rather than declared as bundle payloads. A payload is a thing that can be forgotten — which is precisely how `mbanative.dll` went missing.

**That one `media\enlist.ico` is every installer's icon, and each one is set somewhere different** — which is how they drifted apart and why `verify.ps1` now checks all of them:

| Where Windows shows it | Set by |
|---|---|
| The wizard's title bar and taskbar button | `Icon=` in `WizardWindow.xaml` |
| `enList-<version>-Setup.exe` in Explorer, and the bundle's entry in Installed apps | `IconSourceFile` on the `Bundle` element (`src\Bundle.wxs`) |
| `Enlist.Installer.Ba.exe` in Task Manager | `<ApplicationIcon>` in `Enlist.Installer.Ba.csproj` — the one that was missed, so the bootstrapper sat in Task Manager with the generic glyph beside a setup.exe carrying the real one |
| Each MSI's entry in Installed apps, when installed on its own with `msiexec` | `<Icon>` plus `ARPPRODUCTICON` in each package's `.wxs` |
| The running services and runners in Task Manager — `Enlist.ControlPlane.exe`, `Enlist.Portal.exe`, `enlist-agent.exe`, and both `enlist-runner.exe` | `<ApplicationIcon>` in each project, from a second copy in `src\branding` (its README says why there are two) |

`build.ps1` passes the file to every WiX build as `IconFile`. The check compares the icon's image bytes, not how it looks: rendering an icon and comparing pixels could not tell the generic application icon from this one. If Explorer still shows the old icon after a rebuild, that is the shell's icon cache, which is keyed by path — copy the file somewhere new to see what it really carries. Task Manager keeps a copy of its own on top of that, for as long as it is open, so close and reopen it before believing it.

Note that a dark theme is not a background colour: every input, button, grid and header needs its own template, or it stays Windows-default white on a dark page.

## Building it

`installer\build.ps1` builds this first and passes its output folder to the bundle as `BaDir`, so the ordinary build covers it. On its own:

```powershell
dotnet build ba\Enlist.Installer.Ba\Enlist.Installer.Ba.csproj -c Release
```

## The pages

Welcome, install type, prerequisites, control plane, database, portal, agent, ready. Which of them appear is `InstallPlan.Pages()` — a Server install has no agent page, an agent-only install has no database page, and the step counter in the banner counts the pages this plan will actually show rather than all of them.

**Choosing a container engine brings up the runner image note.** The installer does not carry the runner image — it is a separate download, `enlist-runner-<version>.tar` — and the agent never pulls one, so an agent installed with an engine and nothing loaded fails every container application. So the Agent page replaces its grey "leave the engine empty" hint with an amber note naming the image and the exact load command, and the Finish page repeats it in full after a successful install. Both come from `InstallPlan.RunnerImageNote` (brief and full), so the wording is tested and follows what was typed; an image that is not `enlist/runner:<tag>` gets told to load *that* image, with no download file it does not have. The window is 660 high rather than 620 for it: the note first ran past the bottom of the Agent page, which does not scroll.

Passwords are handled in `WizardWindow.xaml.cs`, which is the one thing that belongs in code-behind: `PasswordBox.Password` is deliberately not a dependency property, so WPF will not let a password into the binding system where a snapshot of the visual tree could reach it.

## After the packages: the part only a bootstrapper can do

The MSIs lay down files and create **stopped** services, and nothing else. Everything in section 8 happens here, in every display mode — a silent install needs its credentials as much as a watched one, so `InstallPlan.FromVariables` rebuilds the plan from the bundle's own variables when there is no wizard to have assembled one.

`PostInstall.Steps(plan, executedPackages)` returns them in order, and the order is a dependency chain:

| Step | Runs | When |
|---|---|---|
| `GrantCertificateAccess` | `<host>.exe grant-certificate-access` | that component's package **ran**, and it has a TLS certificate |
| `ApplySchema` | `Enlist.ControlPlane.exe apply-schema` | the control plane package **ran** |
| `CreatePortalKey` | `Enlist.ControlPlane.exe create-api-key --name portal --role Operator --expires never --replace` | the portal package **ran**, and a control plane is installed |
| `StorePortalKey` | `Enlist.Portal.exe protect <key> --store` | the same |
| `EnrollAgent` | `enlist-agent enroll --join-token <token>` | the agent package **ran**, and a token was given |

**"Ran" means Burn executed that package in this apply** — recorded from `OnExecutePackageBegin` and `OnExecutePackageComplete`, not inferred from the plan. The plan says what the machine *should* have; the engine says what this run *touched*, and only the second is safe to act on.

That distinction is a fix for a real defect. Adding an agent to a Server box is `setup.exe` run again with the agent switched on: the plan still names the control plane and the portal, but Burn executes only the Agent package. Computing steps from the plan re-minted the portal's key, `--replace` revoked the key the *already-running* portal held, and — because the portal reads its key once at startup — it went on presenting a revoked key and got 401 from everything until someone restarted it. Found in the `ApiKeys` table after a live run that otherwise passed.

It is safe to gate this way, not merely convenient, because every enList `ServiceControl` is `Stop="both"`: executing a package stops that component's service, so anything re-keyed is re-read when it next starts. A package Burn did *not* execute still has its service running exactly as it was, and that is the one whose credentials must not move. The two conditions on the portal key are deliberately different for the same reason — the *portal* must have run, because it is what needs the key; the control plane only has to be *installed*, because `create-api-key` needs its database and that is there either way.

**`apply-schema` is new, and the design did not call for it.** `create-api-key` writes to the database directly, so on a fresh machine there is no schema to write to — and the control plane deliberately refuses to migrate itself outside Development, because creating a database and altering tables need rights an application login should not hold. The verb is the deploy step `Deployment-IaC` §1.4 already asked for, run by an installer that is already elevated.

**The portal's key is only minted where the control plane is.** A split-tier portal is told on the Finish page that an Operator must supply one; trying here would mean reaching a database this machine has no business reaching.

**Only enrollment is optional**, and that is about what a failure means rather than importance. A schema that will not apply means the control plane cannot work. Enrollment failing usually means the control plane is not running yet — which on a single-box install it certainly is not, because every service here is created stopped.

### Where the secrets are

**Not in a `PostInstallStep`.** A step is exactly the kind of thing that ends up in a log or a progress message, so it carries no join token, no API key and no connection string. `PostInstallRunner` appends them at the moment of launching:

- the **connection string** goes in the child's *environment*, never its command line, because a command line is readable out of the process list and a SQL password may be in it;
- the **join token** goes on `enroll`'s command line for the second that process lives — as against the service command line, where `sc qc` would show it to anyone, for ever. It is a bundle variable that no package references and that the bundle declares `Hidden`, so Burn's own log prints it as asterisks;
- the **portal's key** is passed to `protect --store` and never logged, shown or written by the bootstrapper. `Enlist.Portal.exe` writes it itself, DPAPI-protected, into its own `appsettings.json` — merged, so the logging configuration that ships beside it survives.

Output is captured but reported only on *failure*, and even then only the trailing lines: `create-api-key`'s successful output **is** the key.

**The ACL is the actual control.** Machine-scope DPAPI is decryptable by any process on the machine, so both secrets are written with inheritance off and an explicit reader list — SYSTEM, Administrators, the writer, and the account the service will run as. That last one is what an installer needs and a service does not: when a service writes its own secret the writer is already the reader, but here the writer is an elevated operator and the service account is a stranger to the file. `WindowsSecrets.ResolveAccount` handles the trap that makes this sharp — `LocalSystem`, the name every service definition uses, is not a name the account database knows.

### The TLS certificate

The last thing that stood between a clean install and a working one. Authentication is `Required`, `Required` refuses plain HTTP off loopback, and both listen defaults are `+` — so a real install serves HTTPS, and HTTPS with no certificate binds and then fails every handshake while complaining about an endpoint.

**By thumbprint**, which ASP.NET Core cannot do for itself: its `Kestrel:Certificates:Default` takes a PFX path or a store lookup by *subject*, and a subject is not unique. The picker lists `LocalMachine\My` with the friendly name, subject, expiry and the last eight of the thumbprint — a machine store routinely holds several certificates identical in every other column. The machine this was built on has two `CN=localhost` entries **both** called "ASP.NET Core HTTPS development certificate", differing only in that one expires shortly; without the expiry and the thumbprint tail there is no way to choose. One within 60 days of expiring is offered with a warning rather than refused.

**Reading a certificate and reading its private key are different permissions**, and that is the trap. The key is a file under `%ProgramData%\Microsoft\Crypto` whose ACL names only whoever imported it, so a certificate installed by an administrator and served by `NETWORK SERVICE` — the default for both hosts — fails every handshake with nothing about the certificate looking wrong. `grant-certificate-access` runs first among the post-install steps for exactly that reason.

## What is not here yet

Nothing blocking an install. Code signing (section 12 item 7) is the remaining open decision, and it needs a certificate purchase rather than a design.
