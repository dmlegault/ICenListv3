using System.Diagnostics;

using Enlist.Agent.Configuration;
using Enlist.TestSupport;

namespace Enlist.Agent.Tests;

/// <summary>
/// Proves the actual gap this hardening pass closes: before it, a runner that crashed on its own
/// (not asked to stop) just stayed dead — nothing noticed, nothing restarted it. These tests kill the
/// real runner process out from under a real AgentHost and check it comes back on its own, and that
/// a genuinely broken app stops being retried instead of crash-looping forever.
/// </summary>
public sealed class AgentCrashRestartTests : IAsyncLifetime
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "enlist-crash-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _appName = RepoPaths.UniqueAppName();
    private AgentHost? _host;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
        }

        // Scoped to this test's own unique app name — see RepoPaths.UniqueAppName for why that matters
        // once multiple test classes are killing/spawning like-named processes in parallel.
        foreach (var leaked in Process.GetProcessesByName(_appName))
        {
            try
            {
                leaked.Kill();
            }
            catch
            {
            }
        }

        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch
        {
        }
    }

    private AgentHost CreateHost(AgentHostOptions options)
    {
        var app = new ApplicationAssignment { Name = _appName, Path = RepoPaths.SampleServiceDir() };
        var assignments = new AgentAssignments { Applications = { app } };
        _host = new AgentHost(assignments, RepoPaths.RunnerBinDirectory(), Path.Combine(_dataRoot, "Runners"), Path.Combine(_dataRoot, "Logs"), options);
        return _host;
    }

    [Fact]
    public async Task Killing_the_runner_process_directly_causes_the_agent_to_restart_it_on_its_own()
    {
        var options = new AgentHostOptions
        {
            RestartBaseDelay = TimeSpan.FromMilliseconds(200),
            RestartMaxDelay = TimeSpan.FromMilliseconds(500),
            RestartSettledDuration = TimeSpan.FromSeconds(30), // long enough that this one crash never "settles" mid-test
            MaxRestartAttempts = 5,
        };
        var host = CreateHost(options);
        await host.StartAsync();

        var originalPid = host.Instances[_appName].Pid;

        // Direct OS kill — not a ShutdownCommand, not agent-initiated. Simulates the runner itself
        // crashing (unhandled exception, access violation, whatever) with nobody asking it to stop.
        Process.GetProcessById(originalPid).Kill();

        var newPid = await WaitForDifferentPidAsync(host, _appName, originalPid, TimeSpan.FromSeconds(10));
        Assert.NotEqual(originalPid, newPid);

        // And the restarted instance is actually functional, not just alive — its service runs again.
        var heartbeatSeen = new TaskCompletionSource();
        host.MessageReceived += (_, message) =>
        {
            if (message is Enlist.Runner.Protocol.LogMessage { Source: "Sample Service" } log && log.Text.StartsWith("heartbeat"))
            {
                heartbeatSeen.TrySetResult();
            }
        };
        await heartbeatSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Repeated_rapid_crashes_stop_being_retried_after_the_configured_cap()
    {
        var options = new AgentHostOptions
        {
            RestartBaseDelay = TimeSpan.FromMilliseconds(100),
            RestartMaxDelay = TimeSpan.FromMilliseconds(300),
            RestartSettledDuration = TimeSpan.FromSeconds(30),
            MaxRestartAttempts = 2,
        };
        var host = CreateHost(options);
        await host.StartAsync();

        // Attempt 1 and 2 should both be retried (ConsecutiveFailures 1 and 2, neither > MaxAttempts).
        for (var i = 0; i < options.MaxRestartAttempts; i++)
        {
            var pid = host.Instances[_appName].Pid;
            Process.GetProcessById(pid).Kill();
            var newPid = await WaitForDifferentPidAsync(host, _appName, pid, TimeSpan.FromSeconds(10));
            Assert.NotEqual(pid, newPid);
        }

        // The 3rd crash pushes ConsecutiveFailures to 3, exceeding MaxAttempts (2) — no further restart.
        var lastPid = host.Instances[_appName].Pid;
        Process.GetProcessById(lastPid).Kill();

        await Poll.UntilAsync(
            () => !host.Instances.ContainsKey(_appName),
            TimeSpan.FromSeconds(5),
            "the application was still running after the crash that should have exhausted its retries.");

        // Give it a window comfortably longer than the backoff would have taken, then confirm it
        // really did give up rather than just being slow.
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.False(host.Instances.ContainsKey(_appName));
        Assert.Empty(Process.GetProcessesByName(_appName));
    }

    private static async Task<int> WaitForDifferentPidAsync(AgentHost host, string appName, int previousPid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (host.Instances.TryGetValue(appName, out var instance) && instance.Pid != previousPid)
            {
                return instance.Pid;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException($"No new instance appeared within {timeout.TotalSeconds:0}s (still pid {previousPid} or gone).");
    }
}
