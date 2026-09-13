using System.Runtime.Versioning;

using Enlist.Agent.Configuration;
using Enlist.ControlPlane.Contracts;
using Enlist.Runner.Protocol;
using Enlist.TestSupport;

namespace Enlist.Agent.Tests;

/// <summary>
/// A REAL net472 application, hosted by the REAL net472 runner, driven by a real AgentHost.
///
/// This was the last gap section 9 of the 2026-09-12 review listed, and the reason it stayed open is
/// recorded in RuntimeFlavorRoutingTests' own summary: when that test was written, "no real net472
/// runner build exist[ed] in this repository", so it proved the SELECTION mechanism by registering
/// the net10.0 runner under the net472 flavor name and checking that the registered path was the one
/// used. That is a real and useful test of AgentHost. It is not a test that a net472 plugin can be
/// hosted at all, and it never claimed to be.
///
/// Both halves exist now - src/Enlist.Runner.Legacy builds enlist-runner.exe for net472, and
/// samples/Enlist.Sample.Legacy deploys a genuinely legacy-shaped package to deploy/LegacySample
/// (no .deps.json, which is what runtime-flavor detection keys on) - so the whole chain can be run
/// for real: flavor on the assignment, the legacy runner-bin the agent was given for it, staging a
/// build whose output shape is nothing like the modern runner's, Assembly.LoadFrom discovery under
/// .NET Framework, and the same wire protocol back.
///
/// Windows only, and honestly skipped rather than quietly returning: net472 does not run anywhere
/// else, and a test that passes by doing nothing is worse than no test.
/// </summary>
public sealed class LegacyApplicationEndToEndTests : IAsyncLifetime
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "enlist-legacy-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly string _appName = RepoPaths.UniqueAppName();
    private AgentHost? _host;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            await _host.DisposeAsync();
        }

        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch
        {
            // A runner that has not quite let go of a staged file is not this test's verdict.
        }
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public async Task A_real_net472_application_starts_logs_and_stops_through_the_legacy_runner()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "net472 runs on Windows only.");
        Skip.IfNot(
            File.Exists(Path.Combine(RepoPaths.LegacyRunnerBinDirectory(), "enlist-runner.exe")),
            "The net472 runner is not built. Build src/Enlist.Runner.Legacy first.");
        Skip.IfNot(
            Directory.Exists(RepoPaths.LegacySampleDir()),
            "The net472 sample is not built. Build samples/Enlist.Sample.Legacy first.");

        var app = new ApplicationAssignment
        {
            Name = _appName,
            Path = RepoPaths.LegacySampleDir(),
            RuntimeFlavor = RuntimeFlavors.NetFramework472,
        };

        // The agent's modern runner-bin is the real one, so this test cannot pass by accident on the
        // net10.0 runner: the legacy sample has no .deps.json and the modern runner would not host it.
        // The net472 directory is the one the FLAVOR selects.
        _host = new AgentHost(
            new AgentAssignments { Applications = { app } },
            RepoPaths.RunnerBinDirectory(),
            Path.Combine(_dataRoot, "Runners"),
            Path.Combine(_dataRoot, "Logs"),
            additionalRunnerBinDirectories: new Dictionary<string, string>
            {
                [RuntimeFlavors.NetFramework472] = RepoPaths.LegacyRunnerBinDirectory(),
            });

        var heartbeat = new TaskCompletionSource();
        var services = new List<string>();
        _host.MessageReceived += (_, message) =>
        {
            if (message is ReadyMessage ready)
            {
                lock (services)
                {
                    services.AddRange(ready.Services.Select(s => s.Name));
                }
            }

            if (message is LogMessage { Source: "Legacy Service" } log && log.Text.StartsWith("heartbeat", StringComparison.Ordinal))
            {
                heartbeat.TrySetResult();
            }
        };

        await _host.StartAsync();

        Assert.True(_host.Instances.ContainsKey(_appName), "the net472 application never started.");

        // WHICH runner is actually executing, proved from the running process rather than from the
        // configuration that was meant to select it. This assertion is the whole test: written without
        // it, everything below passed just as happily with the net10.0 runner registered under the
        // net472 flavor - .NET 10 loads the sample's simple net472 assembly perfectly well - so the
        // test proved only that SOMETHING hosted it. The two build outputs are unmistakable:
        // a .exe.config beside the executable is a .NET Framework artifact and exists only in the
        // net472 build, and enlist-runner.runtimeconfig.json only in the net10.0 one. Staging renames
        // both the exe and its .config to the application name (.NET Framework looks a config up
        // strictly by "<exe filename>.config"), so the check follows the executable rather than
        // guessing at a filename.
        var runnerExecutable = StagedRunnerExecutable(_host.Instances[_appName].Pid);
        var runnerDirectory = Path.GetDirectoryName(runnerExecutable)!;
        Assert.True(
            File.Exists(runnerExecutable + ".config"),
            $"the running runner in {runnerDirectory} is not the net472 build - the executable has no .config beside it.");
        Assert.False(
            File.Exists(Path.Combine(runnerDirectory, "enlist-runner.runtimeconfig.json")),
            $"the running runner in {runnerDirectory} is the net10.0 build, so this test would prove nothing about net472.");

        // Discovery worked under Assembly.LoadFrom, including the service whose [EnlistStart] and
        // [EnlistStop] live on a BASE class - the case a load context that stops at the declaring
        // type gets wrong.
        await Poll.UntilAsync(
            () => { lock (services) { return services.Count > 0; } },
            TimeSpan.FromSeconds(30),
            "the legacy runner never reported what it discovered.");

        lock (services)
        {
            Assert.Contains("Legacy Service", services);
            Assert.Contains("Inherited Legacy Service", services);
        }

        // And it is actually RUNNING, not merely loaded: the service's own log line comes back over
        // the same wire protocol the modern runner uses, which is the point of there being one
        // protocol rather than two.
        await heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // A graceful stop, all the way down: the runner process is gone afterwards, not orphaned.
        var pid = _host.Instances[_appName].Pid;
        await _host.StopAsync();
        Assert.False(_host.Instances.ContainsKey(_appName));

        await Poll.UntilAsync(
            () => !ProcessIsAlive(pid),
            TimeSpan.FromSeconds(30),
            $"the net472 runner (pid {pid}) survived the agent's shutdown.");
    }

    /// <summary>The executable the runner process is actually running - its staged copy, whichever runner-bin the flavor selected, renamed to the application name.</summary>
    private static string StagedRunnerExecutable(int pid)
    {
        using var process = System.Diagnostics.Process.GetProcessById(pid);
        return process.MainModule?.FileName
            ?? throw new InvalidOperationException("Could not read the main module of the runner process " + pid + ".");
    }

    private static bool ProcessIsAlive(int pid)
    {
        try
        {
            return !System.Diagnostics.Process.GetProcessById(pid).HasExited;
        }
        catch (ArgumentException)
        {
            // Already gone - GetProcessById throws rather than returning null.
            return false;
        }
    }
}
