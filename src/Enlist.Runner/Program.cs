using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;

using Enlist.Runner.Dev;
using Enlist.Runner.Discovery;
using Enlist.Runner.Execution;
using Enlist.Runner.Hosting;
using Enlist.Runner.Protocol;

namespace Enlist.Runner;

/// <summary>
/// enlist-runner (--pipe &lt;pipeName&gt; | --socket &lt;socketPath&gt; | --listen &lt;port&gt;) --app &lt;appDirectory&gt;
/// enlist-runner --dev --app &lt;appDirectory&gt;      (no agent — see Dev/DevHost.cs)
/// enlist-runner --check --app &lt;appDirectory&gt;    (discovery only — see Dev/CheckReport.cs)
///
/// One process per running application (design doc section 2); see docs/03-architecture/enList-v3-Design.md section 11.
///
/// Three transports, one protocol — they differ only in how the Stream is obtained, and MessageChannel
/// and every message shape are identical across all of them:
///
/// - --pipe   The original named pipe. Still the default everywhere, and the only one used outside a
///            container. The runner dials OUT to a server the agent already created.
/// - --socket A Unix domain socket, same dial-out direction (docs/03-architecture/Container-Story.md §6.1).
/// - --listen The runner is the SERVER: it binds a TCP port and waits for the agent to dial IN. This
///            exists for containers, and the inversion is deliberate — see ListenAsync.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? pipeName = null;
        string? socketPath = null;
        string? listenPort = null;
        string? appPath = null;
        string? logSinkPipe = null;
        string? logSinkTitle = null;

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--pipe")
            {
                pipeName = args[i + 1];
            }
            else if (args[i] == "--socket")
            {
                socketPath = args[i + 1];
            }
            else if (args[i] == "--listen")
            {
                listenPort = args[i + 1];
            }
            else if (args[i] == "--app")
            {
                appPath = args[i + 1];
            }
            else if (args[i] == "--log-sink")
            {
                logSinkPipe = args[i + 1];
            }
            else if (args[i] == "--title")
            {
                logSinkTitle = args[i + 1];
            }
        }

        // Scanned separately over ALL args, because the loop above stops at args.Length - 1: it reads
        // pairs, so a valueless flag in the LAST position would be silently ignored — and
        // `--app <dir> --dev` is the most natural way to type this.
        var dev = Array.IndexOf(args, "--dev") >= 0;
        var check = Array.IndexOf(args, "--check") >= 0;
        var logWindow = Array.IndexOf(args, "--log-window") >= 0;

        // The sink half of --log-window: a second copy of this executable, started with its own
        // console window, that prints whatever the dev host relays over the named pipe. Handled before
        // every other mode because it needs no --app and is not a way of running an application.
        if (logSinkPipe is not null)
        {
            return await LogWindow.RunSinkAsync(logSinkPipe, logSinkTitle ?? "enList output").ConfigureAwait(false);
        }

        if (appPath is null || (!dev && !check && pipeName is null && socketPath is null && listenPort is null))
        {
            Console.Error.WriteLine("Usage:");
            Console.Error.WriteLine("  enlist-runner (--pipe <pipeName> | --socket <socketPath> | --listen <port>) --app <appDirectory>");
            Console.Error.WriteLine("  enlist-runner --dev [--log-window] --app <appDirectory>   run standalone with no agent; --log-window puts log output in its own window");
            Console.Error.WriteLine("  enlist-runner --check --app <appDirectory>   print what enList would discover, then exit");
            return 2;
        }

        // Rejected rather than silently preferring one: being handed more than one endpoint means the
        // launcher is confused about which it actually created, and using the wrong one produces a
        // connect timeout whose cause is invisible from here. --dev and --check join the same rule:
        // each is a complete, mutually exclusive answer to "who is driving this runner?".
        var modes = (pipeName is not null ? 1 : 0) + (socketPath is not null ? 1 : 0) + (listenPort is not null ? 1 : 0)
            + (dev ? 1 : 0) + (check ? 1 : 0);
        if (modes > 1)
        {
            Console.Error.WriteLine("Specify exactly one of --pipe, --socket, --listen, --dev or --check.");
            return 2;
        }

        var port = 0;
        if (listenPort is not null && (!int.TryParse(listenPort, out port) || port is < 1 or > 65535))
        {
            Console.Error.WriteLine($"--listen expects a TCP port between 1 and 65535, got '{listenPort}'.");
            return 2;
        }

        if (!Directory.Exists(appPath))
        {
            Console.Error.WriteLine($"Application directory not found: {appPath}");
            return 2;
        }

        // Normalised because AssemblyLoadContext.LoadFromAssemblyPath demands a rooted path and throws
        // ArgumentException on a relative one. The agent always passes an absolute path, so this never
        // surfaced until --check was pointed at a relative directory from a shell — and the failure is
        // badly misleading: every plugin assembly is skipped, discovery returns empty, and the
        // application reports Running with nothing in it.
        appPath = Path.GetFullPath(appPath);

        // Both developer modes discover with the very same DiscoverPlugins the agent-driven path uses.
        // Nothing below is a parallel implementation — that is the whole point (see DevHost).
        if (check)
        {
            var (checkDiscovery, checkIsolation) = DiscoverPlugins(appPath);
            return CheckReport.Write(appPath, checkDiscovery, checkIsolation);
        }

        if (dev)
        {
            var (devDiscovery, devIsolation) = DiscoverPlugins(appPath);
            return await DevHost
                .RunAsync(devDiscovery, devIsolation, new DirectoryInfo(appPath).Name, logWindow)
                .ConfigureAwait(false);
        }

        Stream transport;
        try
        {
            transport = pipeName is not null ? await ConnectPipeAsync(pipeName).ConfigureAwait(false)
                : socketPath is not null ? await ConnectSocketAsync(socketPath).ConfigureAwait(false)
                : await ListenAsync(port).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or SocketException or IOException)
        {
            var endpoint = pipeName is not null ? $"pipe '{pipeName}'"
                : socketPath is not null ? $"socket '{socketPath}'"
                : $"TCP port {port}";

            Console.Error.WriteLine($"Could not establish the control channel on {endpoint} within {ConnectTimeout.TotalSeconds:0}s: {ex.Message}");
            return 3;
        }

        await using var _ = transport;

        var channel = new MessageChannel<RunnerMessage, AgentCommand>(transport);
        var (discovery, isolation) = DiscoverPlugins(appPath);
        var host = new RunnerHost(discovery, isolation, channel);

        // Installed before anything plugin-authored can run, so a module initializer or static
        // constructor writing to Console can't slip past capture — see design doc section 7.
        var (stdout, stderr) = host.CreateConsoleWriters();
        Console.SetOut(stdout);
        Console.SetError(stderr);

        using var processExit = new CancellationTokenSource();

        // ProcessExit fires for every process exit, including the ordinary one RunAsync itself
        // causes by returning — and it can fire AFTER this method's `using` has already disposed
        // processExit, since ProcessExit runs on CLR shutdown, not on Main returning. An
        // ObjectDisposedException escaping a ProcessExit handler is fatal to the process (a nonzero
        // exit code even though nothing actually went wrong), so both handlers below must treat
        // "already disposed" as "nothing left to cancel", not as an error.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            TryCancel(processExit);
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryCancel(processExit);

        try
        {
            await host.RunAsync(processExit.Token).ConfigureAwait(false);
        }
        finally
        {
            stdout.Flush();
            stderr.Flush();
        }

        return 0;
    }

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private static async Task<Stream> ConnectPipeAsync(string pipeName)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The socket counterpart to the pipe dial-in above — the far end is a UnixSocketChannelListener,
    /// and everything past this point is identical for both transports (see MessageChannel, which only
    /// ever wanted a Stream).
    /// </summary>
    private static async Task<Stream> ConnectSocketAsync(string socketPath)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var cts = new CancellationTokenSource(ConnectTimeout);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cts.Token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (OperationCanceledException)
        {
            socket.Dispose();

            // Surfaced as a TimeoutException so the caller's one catch covers every transport: the pipe
            // client throws TimeoutException for this, while a cancelled socket connect does not.
            throw new TimeoutException($"Connect to '{socketPath}' timed out.");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The container transport, and the one case where the runner is the SERVER rather than the client.
    ///
    /// The inversion is a security decision, not a convenience. The alternative — the agent listening
    /// and the container dialling host.docker.internal — requires the agent to bind an interface
    /// reachable from outside the host, and this protocol has NO authentication whatsoever: anything
    /// that connects can start services and run jobs. Listening INSIDE the container instead lets the
    /// engine publish the port to 127.0.0.1 only, so the channel is unreachable from off-box.
    ///
    /// It also sidesteps a hard platform limit: a Unix domain socket cannot cross a Windows-host →
    /// Linux-container boundary at all. Bind-mounted through Docker Desktop, the socket file arrives
    /// inside the container as an ordinary empty regular file (verified directly, 2026-09-07), so the
    /// --socket transport is unusable for containers on a Windows host.
    ///
    /// Binds all interfaces because the container's own loopback is not reachable from the host; the
    /// container's network namespace plus a loopback-only published port is what actually confines it.
    /// </summary>
    private static async Task<Stream> ListenAsync(int port)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Any, port));
        listener.Listen(1);

        try
        {
            using var cts = new CancellationTokenSource(ConnectTimeout);
            var connection = await listener.AcceptAsync(cts.Token).ConfigureAwait(false);
            return new NetworkStream(connection, ownsSocket: true);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"No agent connected to port {port} in time.");
        }
    }

    private static void TryCancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static (DiscoveryResult Discovery, IsolationInfo Isolation) DiscoverPlugins(string appPath)
    {
        var loadContext = new PluginLoadContext(appPath);
        var loaded = new List<System.Reflection.Assembly>();

        // Skipping a DLL is still the right behaviour — one unloadable file must not fail an entire
        // application — but it is no longer SILENT. Previously a DLL that should have loaded and didn't
        // showed up only as "fewer services and jobs than expected", which is not something anyone can
        // search for or act on: the application reports Running with nothing in it and no error
        // anywhere. These warnings ride the ReadyMessage and are written to the agent log by
        // AgentHost, exactly like the type-load warnings PluginDiscovery.Scan already emits.
        var loadFailures = new List<string>();
        var notLoadable = new List<string>();

        foreach (var dllPath in Directory.EnumerateFiles(appPath, "*.dll", SearchOption.TopDirectoryOnly))
        {
            try
            {
                loaded.Add(loadContext.LoadPlugin(dllPath));
            }
            catch (BadImageFormatException)
            {
                // Usually a native or mixed-mode DLL sitting alongside the managed ones — routine, and
                // there can be many in a publish output.
                //
                // But it is NOT only that: a TRUNCATED or corrupt managed assembly raises the identical
                // exception, and the runtime offers no way to tell the two apart. So these are grouped
                // to keep the noise bounded, and still NAMED — an anonymous count would hide exactly
                // the case worth acting on. Verified: a deliberately truncated real assembly lands here,
                // not in the branch below.
                notLoadable.Add(Path.GetFileName(dllPath));
            }
            catch (Exception ex)
            {
                // Loaded far enough to be recognised as managed and still failed — a missing dependency,
                // or a target framework this runner cannot host. Named individually with the reason.
                loadFailures.Add($"{Path.GetFileName(dllPath)}: {ex.GetType().Name} - {ex.Message}");
            }
        }

        var discovery = PluginDiscovery.Scan(loaded);

        if (loadFailures.Count > 0 || notLoadable.Count > 0)
        {
            var loadWarnings = new List<string>();

            foreach (var failure in loadFailures)
            {
                loadWarnings.Add($"assembly could not be loaded and was skipped - {failure}");
            }

            if (notLoadable.Count > 0)
            {
                // Capped so a publish output full of native libraries produces a readable line rather
                // than a paragraph, while still naming enough to recognise an unexpected entry.
                const int maxNamed = 10;
                var named = string.Join(", ", notLoadable.Take(maxNamed));
                var suffix = notLoadable.Count > maxNamed ? $", and {notLoadable.Count - maxNamed} more" : "";

                loadWarnings.Add(
                    $"{notLoadable.Count} file(s) were skipped as unloadable - normal for native dependencies, " +
                    $"but a corrupt managed assembly is indistinguishable here, so check for anything unexpected: {named}{suffix}.");
            }

            // Load failures first: they explain why the lists below them may be short.
            discovery = discovery with { Warnings = [.. loadWarnings, .. discovery.Warnings] };
        }

        // Read AFTER LoadPlugin/Load have actually run — PrivateResolutionCount accumulates lazily as
        // resolution happens, so reading it any earlier would just be zero. See IsolationInfo for why
        // this is reported at all.
        var isolation = new IsolationInfo(
            loadContext.DepsFilesFound,
            loadContext.ResolverCount,
            loadContext.PrivateResolutionCount,
            loadContext.OrphanedDeps);

        return (discovery, isolation);
    }
}
