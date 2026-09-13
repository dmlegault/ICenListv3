# `ba/` — the bootstrapper application

Three projects. The split between them is one line: **what can be tested, and what cannot.**

| Project | Target | What it is |
|---|---|---|
| `Enlist.Installer.Detection` | `netstandard2.0` + `net472` | Every decision the installer makes. No UI, no engine. |
| `Enlist.Installer.Detection.Tests` | `net10.0` | 46 cases over the above, several against this machine. |
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

**`Main` must not be `[STAThread]`** — the opposite of what a WPF entry point usually wants. The host has already initialised COM on that thread, so marking it STA fails with `RPC_E_CHANGED_MODE` and the process exits before a window can exist. `EnlistBootstrapperApplication.Run` starts its own STA thread for the WPF `Application`, captures that thread's `Dispatcher`, and marshals every engine callback onto it.

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
| `OnApplyComplete` | Result to the wizard, then finish. |
| `OnError` | Shows the message and returns `Result.Ok` — **never retries silently.** An error the operator cannot see, recovered from in a way they did not choose, is how an install ends up in a state nobody can explain. |

**A silent install never reaches the window.** `Display.Full` and `Display.Passive` get a wizard; everything else plans and applies straight through. That is what makes the silent surface in Installer-UI-Design section 10 behave identically with or without a wizard — the window is one way of filling in variables, not a second way of installing.

`Program.Main` wraps the lot in a `try` and appends anything that escapes to `%LocalAppData%\Temp\enlist-bootstrapper-crash.log`. A bootstrapper that dies silently leaves an operator with a setup window that never appeared and a log saying only that a process exited.

## `Enlist.Installer.Detection` — where the decisions live

Four things it finds out about a machine, two it proves about what was typed, and one it computes.

**`RuntimeDetection`** reads the registry shape the .NET installers actually write: one DWORD per version under `SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\<framework>`, with no "latest" to read. `InstalledVersions` enumerates and `Check` compares properly against a minimum — which is precisely the thing the *bundle's* own `RegistrySearch` could not do, and why a machine with 10.0.3 once downloaded 10.0.12 it did not need (see `src/Prerequisites.wxs`). `CheckNetFramework472` covers the legacy runner's requirement.

**`ContainerEngineDetection`** probes `wslc` and `docker` with a 10 second timeout. Neither is required; the first available one becomes the Agent page's default, and none is a perfectly good answer.

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

34 methods, 46 cases. `InstallPlanTests` is the bulk of it — page flow, blocking reasons, and the variable dictionary. `ProbeTests` uses a stub `HttpMessageHandler` for `/health` and `SkippableFact` for anything needing a real SQL Server. `RuntimeDetectionTests` and `ContainerEngineDetectionTests` run against this machine and skip rather than fail where it cannot answer.

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

Note that a dark theme is not a background colour: every input, button, grid and header needs its own template, or it stays Windows-default white on a dark page.

## Building it

`installer\build.ps1` builds this first and passes its output folder to the bundle as `BaDir`, so the ordinary build covers it. On its own:

```powershell
dotnet build ba\Enlist.Installer.Ba\Enlist.Installer.Ba.csproj -c Release
```

## The pages

Welcome, install type, prerequisites, control plane, database, portal, agent, ready. Which of them appear is `InstallPlan.Pages()` — a Server install has no agent page, an agent-only install has no database page, and the step counter in the banner counts the pages this plan will actually show rather than all of them.

Passwords are handled in `WizardWindow.xaml.cs`, which is the one thing that belongs in code-behind: `PasswordBox.Password` is deliberately not a dependency property, so WPF will not let a password into the binding system where a snapshot of the visual tree could reach it.

## What is not here yet

The parts that need a bootstrapper to exist at all, now that one does: minting a join token and exchanging it before the agent service is created, and minting the portal's key with `create-api-key` and storing it with `protect`. Both are section 8 work, and both are why the MSIs stay dumb.

One thing the wizard can now fix that the bundle cannot. `Prerequisites.wxs` still detects ASP.NET Core by matching one exact patch, because a Burn `RegistrySearch` cannot compare versions — so a machine with 10.0.11 downloads 10.0.12 it does not need. `RuntimeDetection.Check` already compares properly, and the Prerequisites page already shows the right answer; what remains is setting a bundle variable from it so the engine agrees with the page.
