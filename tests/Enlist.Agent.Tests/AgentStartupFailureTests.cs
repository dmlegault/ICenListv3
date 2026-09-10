using System.Diagnostics;
using System.Text.Json;

using Enlist.Agent.Configuration;
using Enlist.Agent.Status;
using Enlist.TestSupport;

namespace Enlist.Agent.Tests;

/// <summary>
/// Covers the failure category a CPU-starved box produces right after a patch reboot (tiworker.exe
/// pegging every core): the runner process never manages to connect its pipe within ConnectTimeout,
/// so the runner backend's StartAsync itself throws — no runner instance, no Exited event, nothing for the
/// post-start crash-restart path to hook. Simulated deterministically here by pointing an application
/// at a directory that doesn't exist: enlist-runner.exe validates --app itself and exits almost
/// immediately without ever attempting to connect, which looks identical to "never connected in time"
/// from the agent's side of the pipe.
/// </summary>
public sealed class AgentStartupFailureTests : IAsyncLifetime
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "enlist-startup-fail-tests-" + Guid.NewGuid().ToString("N"));
    private AgentHost? _host;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
        }

        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task A_runner_that_never_connects_in_time_is_retried_and_eventually_given_up_on()
    {
        var appName = RepoPaths.UniqueAppName();
        var options = new AgentHostOptions
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(300),
            RestartBaseDelay = TimeSpan.FromMilliseconds(100),
            RestartMaxDelay = TimeSpan.FromMilliseconds(300),
            RestartSettledDuration = TimeSpan.FromSeconds(30),
            MaxRestartAttempts = 2,
        };

        var app = new ApplicationAssignment { Name = appName, Path = Path.Combine(_dataRoot, "does-not-exist") };
        var assignments = new AgentAssignments { Applications = { app } };
        _host = new AgentHost(assignments, RepoPaths.RunnerBinDirectory(), Path.Combine(_dataRoot, "Runners"), Path.Combine(_dataRoot, "Logs"), options);

        await _host.StartAsync();

        // This app can never succeed (the path is permanently invalid), so it should retry up to
        // MaxAttempts and then settle into Failed — proving the connect-failure path actually loops
        // through the SAME backoff/give-up machinery a post-start crash uses, not a silent one-shot
        // "log and forget."
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        ApplicationStatusEntry? status = null;
        while (DateTime.UtcNow < deadline)
        {
            var json = await File.ReadAllTextAsync(_host.StatusFilePath);
            var snapshot = JsonSerializer.Deserialize<AgentStatusSnapshot>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            status = snapshot!.Applications.FirstOrDefault(a => a.Name == appName);
            if (status?.State == ApplicationState.Failed)
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert.NotNull(status);
        Assert.Equal(ApplicationState.Failed, status!.State);
        Assert.True(status.RestartCount >= options.MaxRestartAttempts, $"expected at least {options.MaxRestartAttempts} recorded attempts, got {status.RestartCount}");
        Assert.False(_host.Instances.ContainsKey(appName));

        // Every attempt staged a runner copy under Runners/<app>; a given-up application leaves nothing behind.
        Assert.False(Directory.Exists(Path.Combine(_dataRoot, "Runners", appName)), "the staging directory of a given-up application was left behind");
    }

    [Fact]
    public async Task A_struggling_application_does_not_block_a_sibling_applications_first_start()
    {
        var options = new AgentHostOptions
        {
            // Generous enough for a REAL connect (SampleService normally connects in well under
            // 300ms), but still short enough that the bad-path app's failure resolves quickly.
            ConnectTimeout = TimeSpan.FromSeconds(1),
            RestartBaseDelay = TimeSpan.FromSeconds(10), // long enough that its retry won't fire within this test's own window
            MaxRestartAttempts = 1,
        };

        var badApp = new ApplicationAssignment { Name = RepoPaths.UniqueAppName(), Path = Path.Combine(_dataRoot, "does-not-exist") };
        var goodApp = new ApplicationAssignment { Name = RepoPaths.UniqueAppName(), Path = RepoPaths.SampleServiceDir() };

        var assignments = new AgentAssignments { Applications = { badApp, goodApp } };
        _host = new AgentHost(assignments, RepoPaths.RunnerBinDirectory(), Path.Combine(_dataRoot, "Runners"), Path.Combine(_dataRoot, "Logs"), options);

        var stopwatch = Stopwatch.StartNew();
        await _host.StartAsync();
        stopwatch.Stop();

        // Before the fix, StartAsync awaited each failing app's ENTIRE retry-and-backoff sequence
        // inline inside its own foreach — badApp alone would have blocked goodApp from even attempting
        // its first start for as long as badApp's backoff took. A comfortable margin under that.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"StartAsync took {stopwatch.Elapsed} - looks like it blocked on the struggling application instead of moving on to its sibling.");

        Assert.True(_host.Instances.ContainsKey(goodApp.Name), "the healthy sibling application should have started despite the other one failing.");
    }
}
