using System.IO.Pipes;

using Enlist.Runner.Legacy.Discovery;
using Enlist.Runner.Legacy.Execution;
using Enlist.Runner.Legacy.Protocol;

namespace Enlist.Runner.Legacy.Dev;

/// <summary>
/// `enlist-runner --dev --app &lt;dir&gt;` for net472 applications — the legacy counterpart to
/// Enlist.Runner/Dev/DevHost.cs, and deliberately kept behaviourally identical to it.
///
/// A .NET Framework plugin author has strictly the worse debugging story of the two: the modern
/// runner at least starts under a familiar `dotnet run`, whereas this one is an exe the agent stages
/// into a directory the author never sees. Giving only the modern runner a dev host would have made
/// net472 the flavour that is harder to develop for, for no reason other than which file the feature
/// landed in first.
///
/// Same constraint as the modern host: no privileged entry point. It stands up a real named pipe
/// in-process and drives RunnerHost with ordinary AgentCommands, so discovery, settings loading and
/// the load context all behave exactly as they do under a real agent.
///
/// KEEP IN STEP with the modern DevHost. This project duplicates the protocol and the host by design
/// (see the csproj header), and this file joins that list — the same class of drift the runner
/// warnings and ProtocolVersion were both added to catch. The net472 differences below are forced,
/// not stylistic: no Task.WaitAsync, no range/index syntax, no char overloads of Split/Join, and
/// MessageChannel here is IDisposable rather than IAsyncDisposable.
/// </summary>
internal static class DevHost
{
    public static async Task<int> RunAsync(
        DiscoveryResult discovery, IsolationInfo isolation, string appLabel, bool useLogWindow)
    {
        // Wraps the REAL Console.Out, captured before RunnerHost redirects it into log capture —
        // otherwise this driver's own output would be posted into the plugin log stream and arrive
        // back as a LogMessage. DevConsole also owns the line editor, so streaming output never lands
        // in the middle of a half-typed command.
        var console = new DevConsole(Console.Out);

        // Opened before anything is printed, so the banner and inventory land in the output window too.
        LogWindow? logWindow = null;
        if (useLogWindow)
        {
            string? failure;
            logWindow = LogWindow.TryStart("enList output - " + appLabel, out failure);
            if (logWindow == null)
            {
                console.WriteLine("  warn --log-window unavailable (" + failure + "); output stays in this window.");
            }
            else
            {
                console.RouteOutputTo(logWindow.TryWriteLine);

                // Local, not routed: this is the note telling you where everything else went.
                console.WriteLocal("enList dev host (net472) - " + appLabel);
                console.WriteLocal("Log output is in the separate window titled \"enList output - " + appLabel + "\".");
                console.WriteLocal("Type commands here. 'help' for the list, Ctrl+C to stop.");
                console.WriteLocal(string.Empty);
            }
        }

        var pipeName = "enlist-dev-" + Guid.NewGuid().ToString("N");

        using (var server = new NamedPipeServerStream(
                   pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        using (var client = new NamedPipeClientStream(
                   ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            // Listen before dialling, or the connect races the server into existence.
            var accept = server.WaitForConnectionAsync();
            await client.ConnectAsync(5000).ConfigureAwait(false);
            await accept.ConfigureAwait(false);

            using (var runnerChannel = new MessageChannel<RunnerMessage, AgentCommand>(client))
            using (var driverChannel = new MessageChannel<AgentCommand, RunnerMessage>(server))
            {
                var host = new RunnerHost(discovery, isolation, runnerChannel);

                var writers = host.CreateConsoleWriters();
                Console.SetOut(writers.Out);
                Console.SetError(writers.Error);

                using (var processExit = new CancellationTokenSource())
                {
                    var driver = new DevDriver(driverChannel, console, processExit);

                    // First Ctrl+C asks nicely so [EnlistStop] runs and its output is still delivered;
                    // a second forces it. Killing on the first press would make the stop path — the
                    // half people get wrong most often — the one part that cannot be debugged here.
                    var interrupts = 0;
                    Console.CancelKeyPress += (_, e) =>
                    {
                        e.Cancel = true;
                        if (Interlocked.Exchange(ref interrupts, 1) == 0)
                        {
                            console.WriteLine();
                            console.WriteLine("Stopping services... press Ctrl+C again to force.");
                            driver.RequestShutdown();
                        }
                        else
                        {
                            console.WriteLine("Forcing exit - in-flight output is lost.");
                            driver.ForceStop();
                        }
                    };

                    // Background, so a parked Console.ReadLine can never hold the process open.
                    var input = new Thread(driver.RunInputLoop) { IsBackground = true, Name = "enlist-dev-input" };
                    input.Start();

                    var receive = driver.ReceiveLoopAsync();

                    try
                    {
                        await host.RunAsync(processExit.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        writers.Out.Flush();
                        writers.Error.Flush();
                    }

                    // Close the runner half so the driver's receive loop sees end-of-stream. Without
                    // this both ends wait on each other and the process hangs after 'quit'.
                    client.Dispose();

                    // Task.WaitAsync does not exist on net472 — WhenAny is the equivalent guard.
                    await Task.WhenAny(receive, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                }
            }
        }

        console.WriteLine();
        console.WriteLine("enList dev host stopped.");

        // Also locally, so the command window does not fall silent when a log window is in use.
        if (logWindow != null)
        {
            console.WriteLocal("enList dev host stopped.");
            logWindow.Dispose();
        }

        return 0;
    }
}

/// <summary>
/// The agent half of the channel: turns typed console input into AgentCommands and renders
/// RunnerMessages back. Mirror of the modern DevDriver — see this file's header before changing
/// either one alone.
/// </summary>
internal sealed class DevDriver
{
    private readonly MessageChannel<AgentCommand, RunnerMessage> _channel;
    private readonly DevConsole _console;
    private readonly CancellationTokenSource _processExit;

    private IReadOnlyList<ServiceInfo> _services = new List<ServiceInfo>();
    private IReadOnlyList<JobInfo> _jobs = new List<JobInfo>();
    private volatile bool _ready;
    private volatile bool _shuttingDown;
    private int _runCounter;

    public DevDriver(
        MessageChannel<AgentCommand, RunnerMessage> channel,
        DevConsole console,
        CancellationTokenSource processExit)
    {
        _channel = channel;
        _console = console;
        _processExit = processExit;
    }

    /// <summary>
    /// Graceful stop, and deliberately NOT a cancellation. The runner's ShutdownCommand path stops
    /// every service — calling [EnlistStop] — and only then drains its outbound pump, so shutdown
    /// output still reaches the console. Cancelling processExit tears the pump down at the same
    /// moment, discarding exactly the messages a developer is trying to watch.
    /// </summary>
    public void RequestShutdown()
    {
        _shuttingDown = true;

        try
        {
            _channel.SendAsync(new ShutdownCommand((int)TimeSpan.FromSeconds(10).TotalMilliseconds))
                .GetAwaiter().GetResult();
            return;
        }
        catch (Exception)
        {
            // The channel is already torn down (the host exited first); the hard stop is all that is
            // left.
        }

        ForceStop();
    }

    /// <summary>The escape hatch for a service that will not stop — loses in-flight output, by definition.</summary>
    public void ForceStop()
    {
        _shuttingDown = true;

        try
        {
            _processExit.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async Task ReceiveLoopAsync()
    {
        while (true)
        {
            RunnerMessage? message;
            try
            {
                message = await _channel.ReceiveAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            if (message is null)
            {
                return;
            }

            try
            {
                await HandleAsync(message).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Handling a message must never take the process down — 'quit' arriving before Ready
                // was processed would write a StartServiceCommand to an already-closed pipe.
                _console.WriteLine("  ERR  dev host: " + ex.GetType().Name + " - " + ex.Message);
                return;
            }
        }
    }

    private async Task HandleAsync(RunnerMessage message)
    {
        switch (message)
        {
            case ReadyMessage ready:
                await OnReadyAsync(ready).ConfigureAwait(false);
                break;

            case LogMessage log:
                _console.WriteLine($"{Tag(log.Level)} [{log.Source}] {log.Text}");
                break;

            case StateChangedMessage state:
                _console.WriteLine($"  ..   {state.TargetKind.ToString().ToLowerInvariant()} '{state.Target}' -> {state.State}");
                break;

            case JobResultMessage result:
                _console.WriteLine(result.Outcome switch
                {
                    JobOutcome.Succeeded => $"  ok   job '{result.Job}' completed in {result.DurationMs} ms",
                    JobOutcome.Cancelled => $"  ..   job '{result.Job}' cancelled after {result.DurationMs} ms",
                    _ => $"  FAIL job '{result.Job}' failed after {result.DurationMs} ms: {result.Error}",
                });
                break;

            case FaultedMessage faulted:
                _console.WriteLine($"  FAIL {faulted.Target ?? "application"}: {faulted.Error}");
                break;
        }
    }

    private async Task OnReadyAsync(ReadyMessage ready)
    {
        _services = ready.Services;
        _jobs = ready.Jobs;
        _ready = true;

        _console.WriteLine();
        _console.WriteLine("=== enList dev host (net472) ==========================================");
        if (!string.IsNullOrWhiteSpace(ready.ApplicationDescription))
        {
            // Bang, not a redundant check: net472's string.IsNullOrWhiteSpace carries no
            // [NotNullWhen(false)] annotation, so the compiler cannot narrow it the way it does in the
            // modern runner's identical line.
            _console.WriteLine(ready.ApplicationDescription!);
        }

        WriteInventory(_console.WriteLine);

        foreach (var warning in ready.Warnings)
        {
            _console.WriteLine("  warn " + warning);
        }

        _console.WriteLine();
        _console.WriteLine("Type 'help' for commands. Ctrl+C stops services and exits.");
        _console.WriteLine("=======================================================================");
        _console.WriteLine();

        // A shutdown can already have been requested by the time Ready lands — 'quit' from a piped
        // stdin arrives almost instantly, and starting services into a closing host throws.
        if (_shuttingDown)
        {
            return;
        }

        // Mirrors an application whose policy DesiredState is Running, which is the state a developer
        // is nearly always trying to reproduce.
        if (_services.Count > 0)
        {
            _console.WriteLine($"Starting {_services.Count} service(s) - as a Running policy would.");
            foreach (var service in _services)
            {
                await _channel.SendAsync(new StartServiceCommand(service.Name)).ConfigureAwait(false);
            }
        }
        else if (_jobs.Count > 0)
        {
            _console.WriteLine("No services to start - this application is jobs only. Use 'run <job>'.");
        }
    }

    /// <summary>
    /// Anything printed because the user typed something — `list`, `help`, a rejected command. It goes
    /// to the command window even when a log window is in use: a reply sent to the OTHER window is one
    /// you have to go looking for, which defeats the split.
    /// </summary>
    private void Respond(string text)
    {
        _console.WriteLocal(text);
    }

    /// <summary>
    /// Takes its writer because it serves both roles: part of the startup transcript (streaming, so the
    /// log window) and the answer to `list` (a response, so the command window).
    /// </summary>
    private void WriteInventory(Action<string> write)
    {
        write(string.Empty);
        write($"Services ({_services.Count}):");
        foreach (var service in _services)
        {
            write($"  - {service.Name}   [{service.TypeName}]");
            if (!string.IsNullOrWhiteSpace(service.Description))
            {
                write("      " + service.Description);
            }
        }

        write($"Jobs ({_jobs.Count}):");
        foreach (var job in _jobs)
        {
            var cron = job.DeclaredCron is null ? "no declared cron" : "cron " + job.DeclaredCron;
            write($"  - {job.Name}   [{job.TypeName}] ({cron})");
            if (!string.IsNullOrWhiteSpace(job.Description))
            {
                write("      " + job.Description);
            }
        }
    }

    /// <summary>
    /// Blocking, and intentionally so — see the background thread it runs on. Console.ReadLine has no
    /// cancellable overload, and a dev tool must never be the reason a process refuses to exit.
    /// </summary>
    public void RunInputLoop()
    {
        while (true)
        {
            string? line;
            try
            {
                line = _console.ReadLine();
            }
            catch (Exception)
            {
                return;
            }

            if (line is null)
            {
                // stdin closed. Leave services running — a redirected stdin is not a request to exit.
                return;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                if (!Dispatch(line.Trim()))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                Respond("  FAIL " + ex.Message);
            }
        }
    }

    /// <summary>Returns false to end the input loop.</summary>
    private bool Dispatch(string line)
    {
        // Split ONLY the verb off the front: service and job names routinely contain spaces
        // ("Order Intake Listener"), so splitting the whole line would make every multi-word target
        // unreachable.
        var space = line.IndexOf(' ');
        var verb = (space < 0 ? line : line.Substring(0, space)).ToLowerInvariant();
        var remainder = space < 0 ? "" : line.Substring(space + 1).Trim();

        // Everything except help and quit needs the ReadyMessage to have landed — otherwise 'list'
        // prints an empty inventory, which reads as "your attributes didn't work" rather than "not
        // known yet".
        if (!_ready && verb != "help" && verb != "?" && verb != "quit" && verb != "exit")
        {
            Respond("  ..   still starting - the application has not reported yet. Try again in a moment.");
            return true;
        }

        switch (verb)
        {
            case "help":
            case "?":
                WriteHelp();
                return true;

            case "list":
            case "services":
            case "jobs":
                WriteInventory(Respond);
                return true;

            case "start":
                return WithTarget(verb, remainder, name => Send(new StartServiceCommand(name)));

            case "stop":
                return WithTarget(verb, remainder, name =>
                    Send(new StopServiceCommand(name, (int)TimeSpan.FromSeconds(10).TotalMilliseconds)));

            case "run":
                return WithTarget(verb, remainder, RunJob);

            case "cancel":
                return WithTarget(verb, remainder, name => Send(new CancelJobCommand(name)));

            case "quit":
            case "exit":
                RequestShutdown();
                return false;

            default:
                Respond($"  FAIL unknown command '{verb}'. Type 'help'.");
                return true;
        }
    }

    private void RunJob(string remainder)
    {
        // Trailing key=value tokens are settings overrides; everything before them is the job name.
        // Peeled off the END so a multi-word job name survives.
        var tokens = new List<string>(remainder.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        var overrides = new Dictionary<string, string>();

        while (tokens.Count > 1)
        {
            var last = tokens[tokens.Count - 1];
            var split = last.IndexOf('=');
            if (split <= 0)
            {
                break;
            }

            overrides[last.Substring(0, split)] = last.Substring(split + 1);
            tokens.RemoveAt(tokens.Count - 1);
        }

        var job = string.Join(" ", tokens);
        var runId = "dev-" + Interlocked.Increment(ref _runCounter);

        if (overrides.Count > 0)
        {
            // Settings here are OVERRIDES only. The runner still loads {TypeFullName}.settings.json
            // from beside the DLL and merges these on top, so passing none reproduces a production run
            // exactly — this is the same override channel a real agent has, not a dev-only shortcut.
            Respond("  ..   overriding " + string.Join(", ", overrides.Keys));
        }

        Send(new RunJobCommand(job, runId, overrides));
    }

    private bool WithTarget(string verb, string target, Action<string> action)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            Respond($"  FAIL '{verb}' needs a name - try 'list' to see them.");
            return true;
        }

        action(target);
        return true;
    }

    /// <summary>
    /// Tolerant of a channel that has already gone: the input thread runs independently of the host,
    /// so a command typed while the runner is shutting down would otherwise be reported to the
    /// developer as a failure of THEIR command.
    /// </summary>
    private void Send(AgentCommand command)
    {
        try
        {
            _channel.SendAsync(command).GetAwaiter().GetResult();
        }
        catch (Exception) when (_shuttingDown)
        {
            Respond("  ..   ignored - the host is shutting down.");
        }
    }

    private void WriteHelp()
    {
        Respond(string.Empty);
        Respond("  list                 show discovered services and jobs");
        Respond("  start <service>      start a service");
        Respond("  stop <service>       stop a service");
        Respond("  run <job> [k=v ...]  run a job now, optionally overriding settings");
        Respond("  cancel <job>         cancel a run already in flight");
        Respond("  quit                 stop services and exit");
        Respond(string.Empty);
        Respond("  Names are exact and may contain spaces:  run Nightly Reconciliation");
        Respond("  Settings go last:                        run Nightly Reconciliation retries=3");
        Respond(string.Empty);
    }

    private static string Tag(LogLevel level)
    {
        switch (level)
        {
            case LogLevel.Trace:
            case LogLevel.Debug:
                return "  dbg ";
            case LogLevel.Information:
                return "  info";
            case LogLevel.Warning:
                return "  warn";
            default:
                return "  ERR ";
        }
    }
}
