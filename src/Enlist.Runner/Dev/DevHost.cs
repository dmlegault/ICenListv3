using System.IO.Pipes;

using Enlist.Runner.Discovery;
using Enlist.Runner.Execution;
using Enlist.Runner.Protocol;

namespace Enlist.Runner.Dev;

/// <summary>
/// `enlist-runner --dev --app &lt;dir&gt;` — runs an application with NO agent, control plane, portal or
/// database, so a plugin author can press F5 in their own project and hit a breakpoint in
/// [EnlistStart] on the first pass instead of attaching to a grandchild process after the fact.
///
/// The important constraint: this is NOT a second way to execute plugin code. It stands up a real
/// named pipe in-process and speaks the ordinary protocol over it, so RunnerHost, PluginDiscovery,
/// SettingsLoader and PluginLoadContext all run exactly as they do under a real agent — the driver
/// here is simply a very small agent. MessageChannel's own doc comment anticipates precisely this
/// ("a test harness or the future agent is the mirror image, MessageChannel&lt;AgentCommand,
/// RunnerMessage&gt;").
///
/// That constraint matters more than the convenience does. This repository already carries several
/// deliberate duplications that drift when a fix lands on one side only (two runner builds; the
/// portal's conflict detector mirroring the control plane). A dev host with its own private path
/// into RunnerHost would join that list, and a debugging host that behaves differently from
/// production is worse than no debugging host at all — so it gets no privileged entry point, and
/// every action below travels as an ordinary AgentCommand.
/// </summary>
internal static class DevHost
{
    public static async Task<int> RunAsync(
        DiscoveryResult discovery, IsolationInfo isolation, string appLabel, bool useLogWindow)
    {
        // Wraps the REAL Console.Out, captured before RunnerHost redirects it into log capture — a
        // Console.WriteLine past that point would post the driver's own UI into the plugin log stream
        // and it would arrive back through the channel as a LogMessage.
        //
        // DevConsole also owns the line editor: streaming log output and a half-typed command share
        // one lock, so output never lands in the middle of what you are typing. See DevConsole.
        var console = new DevConsole(Console.Out);

        // Opened before anything is printed, so the banner and the discovered inventory land in the
        // output window too rather than being split across both.
        LogWindow? logWindow = null;
        if (useLogWindow)
        {
            logWindow = LogWindow.TryStart($"enList output - {appLabel}", out var failure);
            if (logWindow is null)
            {
                console.WriteLine($"  warn --log-window unavailable ({failure}); output stays in this window.");
            }
            else
            {
                console.RouteOutputTo(logWindow.TryWriteLine);

                // Local, not routed: this is the note telling you where everything else went, so
                // sending it through the sink would leave this window blank and unexplained.
                console.WriteLocal($"enList dev host - {appLabel}");
                console.WriteLocal($"Log output is in the separate window titled \"enList output - {appLabel}\".");
                console.WriteLocal("Type commands here. 'help' for the list, Ctrl+C to stop.");
                console.WriteLocal(string.Empty);
            }
        }

        // Unique per session: two projects debugged at once must not collide, and a name left over
        // from a crashed session must never be reconnected to.
        var pipeName = "enlist-dev-" + Guid.NewGuid().ToString("N");

        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        // Listen before dialling, or the connect races the server into existence.
        var accept = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000).ConfigureAwait(false);
        await accept.ConfigureAwait(false);

        await using var runnerChannel = new MessageChannel<RunnerMessage, AgentCommand>(client);
        await using var driverChannel = new MessageChannel<AgentCommand, RunnerMessage>(server);

        var host = new RunnerHost(discovery, isolation, runnerChannel);

        var (stdout, stderr) = host.CreateConsoleWriters();
        Console.SetOut(stdout);
        Console.SetError(stderr);

        using var processExit = new CancellationTokenSource();
        var driver = new DevDriver(driverChannel, console, processExit);

        // First Ctrl+C asks nicely so [EnlistStop] runs and its output is still delivered; a second
        // forces it, for the service that ignores its stopping token. Killing on the first press would
        // make the stop path — the half people get wrong most often — the one part of an application
        // that cannot be debugged here.
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

        // Background, so a parked Console.ReadLine can never hold the process open: a background
        // thread does not keep the CLR alive, and a blocking console read cannot be cancelled.
        var input = new Thread(driver.RunInputLoop) { IsBackground = true, Name = "enlist-dev-input" };
        input.Start();

        var receive = driver.ReceiveLoopAsync();

        try
        {
            await host.RunAsync(processExit.Token).ConfigureAwait(false);
        }
        finally
        {
            stdout.Flush();
            stderr.Flush();
        }

        // Close the runner half explicitly. The driver's receive loop is parked in ReceiveAsync on the
        // server half and only returns at end-of-stream, so without this both ends wait on each other
        // and the process hangs after 'quit' — the pipes are only closed by the `using` blocks below,
        // which cannot run until this method returns.
        client.Dispose();

        try
        {
            await receive.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Nothing left worth waiting for — every service is already stopped by this point, and a
            // dev tool must not be the reason a developer has to kill a terminal.
        }

        console.WriteLine();
        console.WriteLine("enList dev host stopped.");

        // Also locally, so the command window does not simply fall silent when a log window is in use.
        if (logWindow is not null)
        {
            console.WriteLocal("enList dev host stopped.");
        }

        // Closing the pipe is what tells the log window the session is over — it then says so and
        // stays open, so the log of what just happened is still readable.
        logWindow?.Dispose();
        return 0;
    }
}

/// <summary>
/// The agent half of the channel: turns typed console input into AgentCommands and renders
/// RunnerMessages back. Deliberately thin — everything it knows about the application arrives in the
/// ReadyMessage, exactly as a real agent's knowledge does.
/// </summary>
internal sealed class DevDriver
{
    private readonly MessageChannel<AgentCommand, RunnerMessage> _channel;
    private readonly DevConsole _console;
    private readonly CancellationTokenSource _processExit;

    private IReadOnlyList<ServiceInfo> _services = [];
    private IReadOnlyList<JobInfo> _jobs = [];
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
    /// Graceful stop, and deliberately NOT a cancellation.
    ///
    /// The runner's ShutdownCommand path stops every service — calling [EnlistStop] — and only then
    /// completes its outbound queue and drains the pump, so the stop logs and StateChanged messages
    /// still reach the console. Cancelling processExit instead reaches StopAllAsync too, but it
    /// cancels PumpOutboundAsync at the same moment, so every message that shutdown produces is
    /// discarded before it can be written. The visible symptom is a dev session that exits cleanly and
    /// shows nothing at all for [EnlistStop] — which reads as "my stop method never ran".
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
            // The channel is already torn down (the host exited first). Nothing can be sent, so the
            // hard stop below is all that is left.
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
                // Handling a message must never take the process down. The case that actually happened:
                // 'quit' arriving before ReadyMessage was processed, so the auto-start below wrote
                // StartServiceCommand to a pipe the host had already closed, and the IOException
                // escaped ReceiveLoopAsync as an unhandled exception — only ReceiveAsync was guarded,
                // not the handling.
                _console.WriteLine($"  ERR  dev host: {ex.GetType().Name} - {ex.Message}");
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
        _console.WriteLine("=== enList dev host ===================================================");
        if (!string.IsNullOrWhiteSpace(ready.ApplicationDescription))
        {
            _console.WriteLine(ready.ApplicationDescription);
        }

        WriteInventory(_console.WriteLine);

        foreach (var warning in ready.Warnings)
        {
            _console.WriteLine($"  warn {warning}");
        }

        _console.WriteLine();
        _console.WriteLine("Type 'help' for commands. Ctrl+C stops services and exits.");
        _console.WriteLine("=======================================================================");
        _console.WriteLine();

        // A shutdown can already have been requested by the time Ready lands — 'quit' from a piped
        // stdin arrives almost instantly. Starting services into a closing host would be pointless at
        // best and, since the host may already have closed the channel, throws.
        if (_shuttingDown)
        {
            return;
        }

        // Mirrors an application whose policy DesiredState is Running, which is the state a developer
        // is nearly always trying to reproduce. 'stop <service>' is one command away for the rest.
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
    /// to the command window even when a log window is in use.
    ///
    /// Streaming output (log lines, state changes, job results) is the opposite: it belongs in the log
    /// window precisely so it stays out of the way. But a response to a command you just typed, sent to
    /// the other window, is a reply you have to go looking for — which defeats the split.
    /// </summary>
    private void Respond(string text) => _console.WriteLocal(text);

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
                write($"      {service.Description}");
            }
        }

        write($"Jobs ({_jobs.Count}):");
        foreach (var job in _jobs)
        {
            var cron = job.DeclaredCron is null ? "no declared cron" : $"cron {job.DeclaredCron}";
            write($"  - {job.Name}   [{job.TypeName}] ({cron})");
            if (!string.IsNullOrWhiteSpace(job.Description))
            {
                write($"      {job.Description}");
            }
        }
    }

    /// <summary>
    /// Blocking, and intentionally so — see the background thread it runs on. Console.ReadLine has no
    /// cancellable overload on either target framework, and a dev tool must never be the reason a
    /// process refuses to exit.
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
                // stdin closed (piped input finished, or no console attached). Leave services running
                // rather than shutting down — a redirected stdin is not a request to exit, so
                // `enlist-runner --dev < script.txt` still behaves.
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
                Respond($"  FAIL {ex.Message}");
            }
        }
    }

    /// <summary>Returns false to end the input loop.</summary>
    private bool Dispatch(string line)
    {
        // Split ONLY the verb off the front. Service and job names routinely contain spaces
        // ("Order Intake Listener"), so the remainder of the line is the name — splitting the whole
        // line on spaces would make every multi-word target unreachable.
        var space = line.IndexOf(' ');
        var verb = (space < 0 ? line : line[..space]).ToLowerInvariant();
        var remainder = space < 0 ? "" : line[(space + 1)..].Trim();

        // Everything except help and quit needs the ReadyMessage to have landed. Without this guard a
        // 'list' typed during startup prints "Services (0): Jobs (0):" — which is not "not yet known",
        // it reads as "your attributes didn't work", and that is the exact wrong thing to tell someone
        // whose application is fine.
        if (!_ready && verb is not ("help" or "?" or "quit" or "exit"))
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
        // Peeled off the END so a job name containing '=' is still reachable and, more importantly,
        // so a multi-word name survives.
        var tokens = remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var overrides = new Dictionary<string, string>();

        while (tokens.Count > 1)
        {
            var last = tokens[^1];
            var split = last.IndexOf('=');
            if (split <= 0)
            {
                break;
            }

            overrides[last[..split]] = last[(split + 1)..];
            tokens.RemoveAt(tokens.Count - 1);
        }

        var job = string.Join(' ', tokens);
        var runId = $"dev-{Interlocked.Increment(ref _runCounter)}";

        if (overrides.Count > 0)
        {
            // Settings here are OVERRIDES only. The runner still loads {TypeFullName}.settings.json
            // from beside the DLL and merges these on top (RunnerHost.MergeSettings), so passing none
            // reproduces a production run exactly — and this is the same override channel a real
            // agent has, not a dev-only shortcut.
            Respond($"  ..   overriding {string.Join(", ", overrides.Keys)}");
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
    /// so a command typed (or piped) while the runner is shutting down would otherwise throw on a
    /// broken pipe and be reported to the developer as a failure of THEIR command.
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

    private static string Tag(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "  dbg ",
        LogLevel.Information => "  info",
        LogLevel.Warning => "  warn",
        _ => "  ERR ",
    };
}
