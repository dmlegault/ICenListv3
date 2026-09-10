using System.IO.Pipes;

using Enlist.Runner.Legacy.Dev;
using Enlist.Runner.Legacy.Discovery;
using Enlist.Runner.Legacy.Execution;
using Enlist.Runner.Legacy.Hosting;
using Enlist.Runner.Legacy.Protocol;

namespace Enlist.Runner.Legacy;

/// <summary>
/// enlist-runner-legacy --pipe &lt;pipeName&gt; --app &lt;appDirectory&gt;
/// enlist-runner-legacy --dev --app &lt;appDirectory&gt;      (no agent — see Dev/DevHost.cs)
/// enlist-runner-legacy --check --app &lt;appDirectory&gt;    (discovery only — see Dev/CheckReport.cs)
///
/// The net472 counterpart to Enlist.Runner — same CLI shape,
/// same wire protocol, same attribute-based discovery, same one-process-per-running-application
/// model. Staged and launched by Enlist.Agent exactly like the modern runner (RunnerStaging has no
/// idea which one it's copying — see AgentHost's RuntimeFlavor-keyed runner-bin selection); the only
/// difference visible to the agent is which --runner-bin/--legacy-runner-bin directory this came from.
///
/// The one real difference from the modern runner is Hosting/LegacyPluginLoadContext — see its own
/// doc comment for what net472 can't provide here that AssemblyLoadContext gives the modern runner.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? pipeName = null;
        string? appPath = null;
        string? logSinkPipe = null;
        string? logSinkTitle = null;

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--pipe")
            {
                pipeName = args[i + 1];
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

        // The sink half of --log-window: a second copy of this executable with its own console window.
        // Handled first because it needs no --app and is not a way of running an application.
        if (logSinkPipe != null)
        {
            return await LogWindow.RunSinkAsync(logSinkPipe, logSinkTitle ?? "enList output").ConfigureAwait(false);
        }

        if (appPath is null || (!dev && !check && pipeName is null))
        {
            Console.Error.WriteLine("Usage:");
            Console.Error.WriteLine("  enlist-runner-legacy --pipe <pipeName> --app <appDirectory>");
            Console.Error.WriteLine("  enlist-runner-legacy --dev [--log-window] --app <appDirectory>   run standalone with no agent; --log-window puts log output in its own window");
            Console.Error.WriteLine("  enlist-runner-legacy --check --app <appDirectory>   print what enList would discover, then exit");
            return 2;
        }

        // Each mode is a complete, mutually exclusive answer to "who is driving this runner?".
        var modes = (pipeName is not null ? 1 : 0) + (dev ? 1 : 0) + (check ? 1 : 0);
        if (modes > 1)
        {
            Console.Error.WriteLine("Specify exactly one of --pipe, --dev or --check.");
            return 2;
        }

        if (!Directory.Exists(appPath))
        {
            Console.Error.WriteLine($"Application directory not found: {appPath}");
            return 2;
        }

        // Normalised for the same reason the modern runner does it: the load context needs a rooted
        // path, the agent always passes one, and a relative --app from a shell would otherwise skip
        // every plugin assembly and report an application that is running but empty.
        appPath = Path.GetFullPath(appPath);

        // Both developer modes discover through the very same DiscoverPlugins the agent-driven path
        // uses — neither is a parallel implementation. See Dev/DevHost.cs.
        if (check)
        {
            var checkResult = DiscoverPlugins(appPath);
            return CheckReport.Write(appPath, checkResult.Discovery, checkResult.Isolation);
        }

        if (dev)
        {
            var devResult = DiscoverPlugins(appPath);
            return await DevHost
                .RunAsync(devResult.Discovery, devResult.Isolation, new DirectoryInfo(appPath).Name, logWindow)
                .ConfigureAwait(false);
        }

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)TimeSpan.FromSeconds(10).TotalMilliseconds).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine($"Could not connect to pipe '{pipeName}' within 10s.");
            return 3;
        }

        using var channel = new MessageChannel<RunnerMessage, AgentCommand>(pipe);
        var (discovery, isolation) = DiscoverPlugins(appPath);
        var host = new RunnerHost(discovery, isolation, channel);

        // Installed before anything plugin-authored can run, so a module initializer or static
        // constructor writing to Console can't slip past capture — see design doc section 7.
        var (stdout, stderr) = host.CreateConsoleWriters();
        Console.SetOut(stdout);
        Console.SetError(stderr);

        using var processExit = new CancellationTokenSource();

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
        var loadContext = new LegacyPluginLoadContext(appPath);
        var loaded = new List<System.Reflection.Assembly>();

        // Mirrors Enlist.Runner/Program.cs exactly: skipping an unloadable DLL is still right — one bad
        // file must not fail an application — but it must not be SILENT, or the application reports
        // Running with nothing in it and no error anywhere. Kept in step with the modern runner
        // deliberately; the two discovery paths having different diagnostics would mean a net472
        // application is harder to debug than a net10.0 one for no reason.
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
                // Usually a native or mixed-mode DLL — but a corrupt managed assembly raises the
                // identical exception and cannot be told apart, so these are grouped yet still named.
                notLoadable.Add(Path.GetFileName(dllPath));
            }
            catch (Exception ex)
            {
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
                const int maxNamed = 10;
                var named = string.Join(", ", notLoadable.Take(maxNamed));
                var suffix = notLoadable.Count > maxNamed ? $", and {notLoadable.Count - maxNamed} more" : "";

                loadWarnings.Add(
                    $"{notLoadable.Count} file(s) were skipped as unloadable - normal for native dependencies, " +
                    $"but a corrupt managed assembly is indistinguishable here, so check for anything unexpected: {named}{suffix}.");
            }

            discovery = discovery with { Warnings = [.. loadWarnings, .. discovery.Warnings] };
        }

        // Honest zeros, not fabricated metrics — see LegacyPluginLoadContext's own doc comment for why
        // this runner has no resolver-based isolation to report on.
        var isolation = new IsolationInfo(DepsFilesFound: 0, ResolverCount: 0, PrivateResolutionCount: 0, OrphanedDeps: []);

        return (discovery, isolation);
    }
}
