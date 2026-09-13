# enList Application Developer Guide

How to write, run, debug and diagnose an enList application **without** standing up a control plane, a portal, an agent, a database, or a deployment.

Every command below was executed against this repository and its real output pasted in. Companion to [`Developer-Setup-Guide.md`](../01-start-here/Developer-Setup-Guide.md) (which covers running the *platform*) and [`Container-Developer-Guide.md`](Container-Developer-Guide.md) (which covers running applications in containers).

---

## 1. What an enList application actually is

A class library. Not an executable.

There is no `Program.cs`, no `Main`, no host builder. You write plain classes, mark them with attributes, and the enList **runner** discovers them by reflection at load time:

```csharp
[EnlistService("Order Intake Listener", Description = "Watches the inbound order queue.")]
public sealed class OrderIntakeListener
{
    [EnlistStart] public Task Start(CancellationToken stopping, Action<string> log) { ... }
    [EnlistStop]  public void Stop() { ... }
}
```

The attributes come from [`contracts/EnlistAttributes.cs`](../../contracts/EnlistAttributes.cs), pulled in **as source** via a `.props` import — never as a package or project reference. The runner matches attributes by *type name string*, not by type identity, which is exactly what lets a net472 application and a net10.0 application share one contract with no shared binary. See [`LLD.md` §6](../03-architecture/LLD.md#6-runner-plugin-discovery--attribute-matching-by-name).

This is why debugging used to hurt: **a class library has nothing to press F5 on.** Your code only ran when an agent staged a runner, launched it as a grandchild process, and handed it your directory over a named pipe.

## 2. The inner loop, before and after

| | Before | Now |
|---|---|---|
| Processes needed | control plane + LocalDB + portal + agent | none |
| Steps to run your code | build → zip → upload → create policy → wait for reconcile | build → F5 |
| Debugger | attach to a grandchild process, racing its startup | it *is* your process; breakpoints hit on the first pass |
| "Why didn't my job appear?" | deploy and read an agent log | `--check`, about one second |
| Your `Console.WriteLine` | captured into the log pipeline | printed to your console |

Two flags on the runner do all of this. Neither is a second implementation: `--dev` stands up a real named pipe in-process and drives the ordinary `RunnerHost` with ordinary `AgentCommand`s, so discovery, settings loading, and the assembly load context behave exactly as they do under a real agent. See the header of [`src/Enlist.Runner/Dev/DevHost.cs`](../../src/Enlist.Runner/Dev/DevHost.cs) for why that constraint is non-negotiable.

---

## 3. `--check` — what would enList see?

Answers the single most common authoring question without running anything. Discovery reflects over types; it never constructs them, so this is safe to point at any build output.

```bash
dotnet build samples/Enlist.Sample.OrderProcessor
src/Enlist.Runner/bin/Debug/net10.0/enlist-runner.exe --check --app deploy/OrderProcessor
```

Real output:

```
Application directory: C:\Users\rober\source\repos\enList_v3\deploy\OrderProcessor
Description:           Processes incoming customer orders - intake, inventory sync, payment authorization, and customer notifications.

Services (4):
  - Order Intake Listener
      type:        Enlist.Sample.OrderProcessor.OrderIntakeListener
      description: Watches the inbound order queue and enqueues new orders for processing.
  ...

Jobs (4):
  - Nightly Reconciliation
      type:        Enlist.Sample.OrderProcessor.NightlyReconciliation
      cron:        0 0 3 * * ?
      description: Reconciles processed orders against payment gateway settlement reports.
  ...

Assembly isolation:
  .deps.json files found:      1
  private resolvers:           1
  privately resolved requests: 0

Warnings: none.

Discovered 4 service(s) and 4 job(s).
```

**Exit codes:** `0` when at least one service or job was discovered, `1` when nothing was. Warnings alone never fail — a publish output full of native DLLs legitimately produces them, and failing on that would train you to ignore the flag. The `1` case is the actionable one, and it prints what to check:

```
Nothing was discovered. enList would report this application as running but empty.
Check that your types are public, non-abstract, carry [EnlistService] or [EnlistJob],
and that a [EnlistStart] / [EnlistExecute] method is present and public.
```

That `1` is worth wiring into a build pipeline. "Running but empty" is the worst failure mode enList has: the portal shows the application green, and nothing whatsoever happens.

The **Warnings** block is the other reason to run this. Load failures are otherwise only ever visible buried in an agent log:

```
Warnings (1):
  - assembly could not be loaded and was skipped - Foo.dll: FileNotFoundException - Could not load file or assembly 'Bar, Version=...'
```

---

## 4. `--dev` — run it, with no agent

```bash
src/Enlist.Runner/bin/Debug/net10.0/enlist-runner.exe --dev --app deploy/OrderProcessor
```

The dev host prints the discovered inventory, **auto-starts every service** — mirroring an application whose policy `DesiredState` is `Running`, which is nearly always the state you are trying to reproduce — and then takes commands:

```
=== enList dev host ===================================================
Processes incoming customer orders - intake, inventory sync, payment authorization, and customer notifications.

Services (4):
  - Order Intake Listener   [Enlist.Sample.OrderProcessor.OrderIntakeListener]
      Watches the inbound order queue and enqueues new orders for processing.
  ...

Type 'help' for commands. Ctrl+C stops services and exits.
=======================================================================

Starting 4 service(s) - as a Running policy would.
  ..   service 'Order Intake Listener' -> Starting
  info [Order Intake Listener] order received (v2) #1
  ..   service 'Order Intake Listener' -> Running
```

### Commands

| Command | Effect |
|---|---|
| `list` | Show discovered services and jobs |
| `start <service>` | `StartServiceCommand` |
| `stop <service>` | `StopServiceCommand` |
| `run <job> [k=v ...]` | `RunJobCommand`, now, with optional settings overrides |
| `cancel <job>` | `CancelJobCommand` — asks a run already in flight to stop; see below |
| `quit` | Stop services and exit |

Names are exact and **may contain spaces** — only the verb is split off the front:

```
run Nightly Reconciliation
run Nightly Reconciliation retries=3 mode=verbose
```

Trailing `key=value` tokens are peeled off the end as settings; everything before them is the job name.

### Stopping a run in progress

A run already underway is stopped by cancelling the `CancellationToken` your `[EnlistExecute]` was handed:

```
run Ledger Rebuild chunks=40
cancel Ledger Rebuild
```

This is not the agent's scheduler-level stop, and the difference matters. The scheduler stop unregisters the job so it stops **firing again**, and needs nothing from your code. This one reaches a run **already executing**. Stopping a job in the portal does both.

The runner cannot stop your work for you — it has no safe way to halt a thread it does not own — so all it does is cancel the token. Whether the run actually stops is yours to implement, at a boundary where stopping is safe:

```csharp
[EnlistExecute]
public void Run(CancellationToken cancelling, IDictionary<string, string> settings)
{
    foreach (var chunk in chunks)
    {
        // The boundary between units of work is where stopping is safe. Checking often enough that a
        // stop feels immediate, rarely enough that nothing is left half-done, is your call to make.
        if (cancelling.IsCancellationRequested)
        {
            return;
        }

        // ... one chunk of work ...
    }
}
```

`samples/Enlist.Sample.OrderProcessor` ships this as **Ledger Rebuild**, and `Enlist.Sample.Legacy` as **Legacy Batch** for net472.

Things worth knowing:

- **Returning and throwing are both fine.** Return normally when you notice, or let `OperationCanceledException` out of a `token.ThrowIfCancellationRequested()` or a cancellable wait — the runner treats them the same.
- **A cancelled run is reported as cancelled, not failed.** `JobResultMessage.Outcome` is `Cancelled` (its own value, alongside `Succeeded` and `Failed`), its state goes to `Stopped` rather than `Faulted`, and the agent logs it at Warning rather than Error. It did not break; it also did not finish, and both halves of that matter to whoever reads the log back.
- **A job that never checks its token runs to completion.** Nothing can stop it, including shutdown — the runner logs that the grace period expired and exits anyway. This is the one thing worth taking from this section.
- **Shutdown cancels in-flight runs too**, then waits out the grace period, rather than exiting out from under work in progress.
- **Cancel with nothing in flight is informational, not an error** — the run having already finished is the ordinary way to get there.

### Typing while output streams

A running application prints continuously, and with a plain console those writes land wherever the cursor is — in the middle of whatever you were typing. The dev host has a small line editor instead: output and keystrokes share one lock, so before a log line prints, your input row is erased; afterwards the prompt is repainted with your partial command and the cursor put back where it was.

The practical effect is that your `> ` line always sits at the bottom, intact, with output scrolling above it. You can type a command at your own pace while a service logs every two seconds.

| Key | |
|---|---|
| `←` `→` `Home` `End` | Move within the line |
| `Backspace` `Delete` | Edit at the cursor |
| `↑` `↓` | Previous / next command — the same job usually gets run several times in a row |
| `Esc` | Clear the line |
| `Enter` | Submit; the command is echoed into the transcript so it stays visible beside the output it caused |

**Under redirection this all switches off.** With a piped stdin or a captured stdout there is no cursor to move and no keystrokes to read, so the host falls back to plain reads and writes — which is what keeps `enlist-runner --dev < script.txt` and scripted sessions working, with no prompt characters polluting the captured output. The same fallback latches if the terminal turns out not to support cursor positioning, so the worst case is the old behaviour rather than a crash.

### Two windows: `--log-window`

If you would rather not share one screen at all, put the log stream in its own console window:

```bash
src/Enlist.Runner/bin/Debug/net10.0/enlist-runner.exe --dev --log-window --app deploy/OrderProcessor
```

A second window opens, titled **enList output — OrderProcessor**. The split is by *what produced the line*, not by severity:

| | Goes to |
|---|---|
| Log lines, state changes, job results, faults — anything arriving on its own schedule | **log window** |
| The startup banner and the inventory discovered at launch | **log window** (part of the startup transcript) |
| `list`, `help`, `unknown command`, `needs a name`, `overriding …` — anything printed *because you typed something* | **command window** |

The rule is that a reply belongs next to the thing it replies to. Sending `list` output to the other window makes it a reply you have to go looking for, which defeats the point of splitting them.

So the command window stays quiet until you ask it something:

```
enList dev host — OrderProcessor
Log output is in the separate window titled "enList output — OrderProcessor".
Type commands here. 'help' for the list, Ctrl+C to stop.

> list

Services (4):
  - Order Intake Listener   [Enlist.Sample.OrderProcessor.OrderIntakeListener]
      Watches the inbound order queue and enqueues new orders for processing.
  ...
Jobs (4):
  - Nightly Reconciliation   [Enlist.Sample.OrderProcessor.NightlyReconciliation] (cron 0 0 3 * * ?)
      Reconciles processed orders against payment gateway settlement reports.
  ...

> run Tax Rate Refresh
> bogus
  FAIL unknown command 'bogus'. Type 'help'.
> quit
enList dev host stopped.
```

`run Tax Rate Refresh` shows no reply here because its result — `ok job 'Tax Rate Refresh' completed in 4 ms` — is a job result, which streams to the log window.

A process owns exactly one console, so the second window is necessarily a second **process**: the host starts another copy of `enlist-runner` in a sink mode (`--log-sink <pipe>`) and relays output to it over a named pipe. Consequences worth knowing:

- **Windows only.** "Give this child its own console window" has no portable equivalent. If the window cannot be started the host says so and keeps everything in one window.
- **The log window outlives the session.** When the host exits it prints `dev host exited — this window is now idle` and waits, so the log of what just happened is still there to read. Press Enter to close it. (A console window dies with its process, so exiting on end-of-stream would erase the log at the exact moment you wanted it.)
- **Closing it early is safe.** The next line the host writes finds the pipe broken, prints `log window closed — output returns to this console`, and carries on in one window. Output is never silently dropped.
- **It is a debugging convenience, and never load-bearing** — every failure path falls back to the single-window behaviour rather than stopping the application you are debugging.

In Visual Studio this is the **enList dev host (log window)** launch profile, which is now the default F5 target.

### Output prefixes

| Prefix | Meaning |
|---|---|
| `info` / `warn` / `ERR` / `dbg` | A `LogMessage` from your code — including anything you wrote to `Console` |
| `..` | A state transition, a cancelled run, or a driver notice |
| `ok` / `FAIL` | A `JobResultMessage` that succeeded or failed, with duration |

### Shutdown

`quit`, or the first `Ctrl+C`, sends a real `ShutdownCommand`: every service is stopped, `[EnlistStop]` runs, and its output is still delivered before the process exits.

```
  ..   service 'Legacy Service' -> Stopping
  info [Legacy Service] Stop() called - the loop's own CancellationToken already handles the rest.
  ..   service 'Legacy Service' -> Stopped

enList dev host stopped.
```

A second `Ctrl+C` forces exit and loses in-flight output. Use it only for a service that ignores its stopping token — which is itself worth knowing about your code.

---

## 5. F5 in Visual Studio

Each sample ships three launch profiles at `Properties/launchSettings.json` — **enList dev host (log window)** (the default), **enList dev host (single window)**, and **enList check (discovery only)**. It runs the runner as an *Executable*, so the debugger attaches to the process from the start and breakpoints in `[EnlistStart]` hit on the first pass — no attaching, no PID hunting, no startup race:

```json
{
  "profiles": {
    "enList dev host (log window)": {
      "commandName": "Executable",
      "executablePath": "../../src/Enlist.Runner/bin/Debug/net10.0/enlist-runner.exe",
      "commandLineArgs": "--dev --log-window --app ../../deploy/OrderProcessor",
      "workingDirectory": "."
    },
    "enList dev host (single window)": {
      "commandName": "Executable",
      "executablePath": "../../src/Enlist.Runner/bin/Debug/net10.0/enlist-runner.exe",
      "commandLineArgs": "--dev --app ../../deploy/OrderProcessor",
      "workingDirectory": "."
    },
    "enList check (discovery only)": {
      "commandName": "Executable",
      "executablePath": "../../src/Enlist.Runner/bin/Debug/net10.0/enlist-runner.exe",
      "commandLineArgs": "--check --app ../../deploy/OrderProcessor",
      "workingDirectory": "."
    }
  }
}
```

**Profile order matters.** Visual Studio runs the *first* profile unless you pick another, so the log-window variant is listed first — with it second, pressing F5 silently gave you the single-window host and no log window at all. If VS has remembered an earlier selection, choose it from the dropdown beside the run button once.

Paths are relative to the project directory and use forward slashes (valid on Windows, and no JSON escaping to get wrong). For your own project, copy this file and change the `OrderProcessor` occurrences to your app's deploy directory — and point `executablePath` at `Enlist.Runner.Legacy` if you target net472 (§7).

Build the runner once before the first F5:

```bash
dotnet build src/Enlist.Runner/Enlist.Runner.csproj
```

**Set the sample as the startup project**, pick a profile from the dropdown, F5.

> Requires the runner to have been built at least once, and rebuilt whenever the runner itself changes. Your *application* rebuilds normally on F5; the runner does not, because it is a separate project the profile merely launches.

---

## 6. Settings

`SettingsLoader` reads a JSON file colocated **next to your DLL**, in this order:

1. `<TypeFullName>.settings.json` — per-type
2. `<AssemblyName>.settings.json` — per-assembly fallback

It flattens into the `IDictionary<string, string> settings` parameter your `[EnlistExecute]`/`[EnlistStart]` methods can bind.

The `run <job> k=v` overrides are **merged on top** of that file, not instead of it (`RunnerHost.MergeSettings`). This matters for fidelity: passing no overrides reproduces a production run exactly, and the override channel itself is the same one a real agent has — not a dev-only shortcut.

```
  ..   overriding mode, retries
  info [Legacy Job] running with 2 setting(s) on net472
  ok   job 'Legacy Job' completed in 0 ms
```

---

## 7. net472 applications

Identical, using the legacy runner instead:

```bash
dotnet build samples/Enlist.Sample.Legacy
src/Enlist.Runner.Legacy/bin/Debug/net472/enlist-runner.exe --check --app deploy/LegacySample
src/Enlist.Runner.Legacy/bin/Debug/net472/enlist-runner.exe --dev   --app deploy/LegacySample
```

The banner says `=== enList dev host (net472) ===` so you always know which flavour you are in. Everything else — commands, prefixes, exit codes, settings, shutdown — behaves the same, deliberately: a .NET Framework author already has the worse debugging story of the two, and there is no reason for the tooling to make that gap wider.

The legacy runner reports honest zeros for assembly isolation, because net472 has no `AssemblyLoadContext` and therefore no resolver-based isolation to report. See `Hosting/LegacyPluginLoadContext.cs`.

---

## 8. What the dev host does NOT reproduce

Read this before concluding your application works.

| Not reproduced | Why, and what to do instead |
|---|---|
| **Cron scheduling** | The runner has no notion of time at all — the *agent* owns scheduling ([`DiscoveredJob.DeclaredCron`](../../src/Enlist.Runner/Discovery/DiscoveredJob.cs)). A job's declared cron is reported and never fires here. Trigger jobs with `run`. |
| **Policy cron overrides** | Live on the policy, applied by the agent. Not visible to a runner at all. |
| **Restart on failure** | Supervision is the agent's job. A service that faults here simply stays faulted. |
| **Log forwarding** | Logs print to your console instead of reaching the control plane and the portal's Logs tab. |
| **Process-tree containment** | The agent's Job Object cleanup is not involved; a child process you spawn and leak will outlive the dev host. |
| **Container isolation** | See [`Container-Developer-Guide.md`](Container-Developer-Guide.md). |
| **Package staging** | You point at a build output directly; no zip, no digest, no extraction. |

Everything in the first column is agent behaviour, and the dev host is deliberately not an agent. What it *does* reproduce faithfully is the part you are actually debugging: discovery, load context, settings, start/stop/execute, and console capture.

### Cron expressions, and a job slower than its schedule

The agent accepts two cron dialects, told apart by counting fields: the standard five (`minute hour day month weekday`) and the six-field seconds-first form (`second minute hour day month weekday`) that `[EnlistJob]`'s examples use — `0 3 * * *` and `0 0 3 * * *` are the same 03:00 daily. Quartz-style `?` is accepted for `*`. The rule is one definition (`CronExpressions` in `Enlist.ControlPlane.Contracts`): the control plane refuses an override that does not parse with a 400 naming the problem, the portal's cron fields turn red before you save, and a declared cron on the attribute that does not parse is written to the agent log at start with the same message and the job left unscheduled.

A job is never run concurrently with itself. If its next tick comes due while the previous run is still going, that tick is **skipped, not queued**, and the application's log says so (`job 'X' was due, but run … is still running — this tick is skipped`). A job that regularly takes longer than its interval therefore runs less often than its cron says; shorten the work or lengthen the schedule. A Stop on the job cancels the run in flight, which also clears the way for the next tick.

Schedules far in the future are fine — a yearly cron sleeps in one-day slices — and a tick whose run command could not be delivered (the runner died at that instant) is logged as a lost firing; the next one is tried normally.

When you need the rest, deploy for real — [`Developer-Setup-Guide.md`](../01-start-here/Developer-Setup-Guide.md) §5–6.

---

## 9. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `--check` finds nothing, but your classes look right | Types not `public`, or abstract, or missing `[EnlistStart]`/`[EnlistExecute]` | Discovery requires a public, non-abstract class with a public marked method and a public parameterless constructor |
| Your `[EnlistStart]`/`[EnlistStop]` live on a base class | Fine — discovery walks the hierarchy (it used to look at declared members only) | See `HeartbeatServiceBase` in the sample |
| Settings come back empty though the file is there | The JSON in `*.settings.json` does not parse | The plugin's own log carries a Warning naming the file (`settings file '…' could not be read and was ignored`); fix the JSON — the run goes ahead without it either way |
| Every assembly skipped, "is not an absolute path" | Fixed — both runners now call `Path.GetFullPath` on `--app`. If it recurs, the normalisation was lost | — |
| `No public parameterless constructor` | Constructor injection is not part of the enList contract | Use a parameterless constructor; take dependencies in the method or from settings |
| `list` prints "still starting" | Typed before the application reported | Wait a moment. Before this guard it printed an empty inventory, which reads as "your attributes didn't work" |
| Nothing happens on a declared cron | Expected — see §8 | `run <job>` |
| A job runs less often than its cron | It takes longer than its interval; ticks that arrive mid-run are skipped, and say so in the application's log | Shorten the work or lengthen the schedule — see §8 |
| `Specify exactly one of --pipe, --socket, --listen, --dev or --check` | More than one driver mode given | Each is a complete answer to "who drives this runner?" |
| Build fails: `enlist-runner.dll` is locked | A running agent holds the runner it staged | Stop the agent before rebuilding — the build otherwise silently keeps the old runner |
| Breakpoint in `[EnlistStart]` never hits | Startup project is not the sample, or the runner was never built | Build `src/Enlist.Runner` once, then set the sample as startup project |

---

## 10. Quick reference

```bash
# What would enList discover here?
enlist-runner --check --app <dir>          # exit 0 = found something, 1 = found nothing

# Run it, no agent, no control plane, no portal, no database
enlist-runner --dev   --app <dir>
enlist-runner --dev --log-window --app <dir>   # log output in its own console window (Windows)

# net472
src/Enlist.Runner.Legacy/bin/Debug/net472/enlist-runner.exe --dev --app <dir>
```

Inside `--dev`: `list`, `start <svc>`, `stop <svc>`, `run <job> [k=v]`, `cancel <job>`, `quit`, `help`.
