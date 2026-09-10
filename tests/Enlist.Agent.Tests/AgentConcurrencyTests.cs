using System.Diagnostics;
using System.Text.Json;

using Enlist.Agent.Configuration;
using Enlist.Agent.Status;
using Enlist.Runner.Protocol;
using Enlist.TestSupport;

namespace Enlist.Agent.Tests;

/// <summary>
/// AgentHost has several independent callers — the reconciliation loop, crash-retries on the thread
/// pool, Process.Exited, the SignalR command handler, and one receive loop per running application —
/// and none of them wait for each other. These tests pin what has to stay true when they collide.
///
/// Every scenario uses a real staged runner process and real timing; the backoff is stretched so a
/// change can be pushed inside it deterministically rather than by luck.
/// </summary>
public sealed class AgentConcurrencyTests : IAsyncLifetime
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "enlist-concurrency-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _appName = RepoPaths.UniqueAppName();
    private AgentHost? _host;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
        }

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

    /// <summary>
    /// A handler throwing on ONE message must not stop the agent hearing that runner. It used to: the
    /// receive loop's single try/catch ended the loop, the runner kept running, and every log line,
    /// state change and job result after that point was silently lost while the application still
    /// reported Running. MessageReceived is the production path's own last step, so a subscriber that
    /// throws exercises exactly the case.
    /// </summary>
    [Fact]
    public async Task A_handler_that_throws_on_one_message_does_not_stop_the_agent_hearing_the_runner()
    {
        var app = new ApplicationAssignment { Name = _appName, Path = RepoPaths.SampleServiceDir() };
        _host = new AgentHost(new AgentAssignments { Applications = { app } }, RepoPaths.RunnerBinDirectory(), Path.Combine(_dataRoot, "Runners"), Path.Combine(_dataRoot, "Logs"));

        var thrown = false;
        var heartbeatsAfterThrow = new TaskCompletionSource();
        _host.MessageReceived += (_, message) =>
        {
            if (message is not LogMessage { Source: "Sample Service" } log || !log.Text.StartsWith("heartbeat"))
            {
                return;
            }

            if (!thrown)
            {
                thrown = true;
                throw new InvalidOperationException("deliberate: a handler fault on one message");
            }

            heartbeatsAfterThrow.TrySetResult();
        };

        await _host.StartAsync();

        // The service logs a heartbeat every couple of seconds; a second one arriving is the proof.
        await heartbeatsAfterThrow.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // And the fault was recorded rather than swallowed — an operator can find out it happened.
        Assert.Contains("deliberate: a handler fault", AgentLog());
    }

    /// <summary>
    /// A crash-retry captures the assignment as it was when the crash happened. If a reconcile
    /// restarts the application during the backoff, the retry must notice and stand down. It used to
    /// go ahead from the stale assignment: on the process backend that collides with the live
    /// instance's staged exe, fails, and flips a perfectly healthy application to Restarting — and to
    /// Failed once the retries run out; on the container backend it starts a second container.
    /// </summary>
    [Fact]
    public async Task A_crash_retry_stands_down_when_a_reconcile_already_restarted_the_application()
    {
        var source = new MutableAssignmentSource(Assignments(cron: "0 0 2 * * ?"));
        _host = CreateHost(source, restartDelay: TimeSpan.FromSeconds(4));
        await _host.StartAsync();

        var crashedPid = _host.Instances[_appName].Pid;
        Process.GetProcessById(crashedPid).Kill();
        await WaitForCrashHandlerAsync();

        // Inside the retry's 4s backoff: a changed assignment (different cron) makes the reconcile loop
        // restart the application itself.
        source.Push(Assignments(cron: "0 0 3 * * ?"));
        var restartedPid = await WaitForDifferentPidAsync(crashedPid, TimeSpan.FromSeconds(10));

        // Past the backoff — the retry has had its turn.
        await Task.Delay(TimeSpan.FromSeconds(6));

        Assert.Equal(restartedPid, _host.Instances[_appName].Pid);
        Assert.Single(Process.GetProcessesByName(_appName));
        Assert.Equal(ApplicationState.Running, await ReportedStateAsync());
        Assert.DoesNotContain("failed to start runner", AgentLog());
    }

    /// <summary>
    /// The other half of the same race: an application disabled DURING its crash backoff must stay
    /// down. The retry used to start it anyway from the captured assignment, because nothing after the
    /// delay re-read what was currently wanted.
    /// </summary>
    [Fact]
    public async Task A_crash_retry_does_not_resurrect_an_application_disabled_during_its_backoff()
    {
        var source = new MutableAssignmentSource(Assignments(cron: "0 0 2 * * ?"));
        _host = CreateHost(source, restartDelay: TimeSpan.FromSeconds(4));
        await _host.StartAsync();

        var crashedPid = _host.Instances[_appName].Pid;
        Process.GetProcessById(crashedPid).Kill();
        await WaitForCrashHandlerAsync();

        source.Push(Assignments(cron: "0 0 2 * * ?", desiredState: DesiredState.Stopped));

        // Past the backoff — the retry has had its turn, and must have declined it.
        await Task.Delay(TimeSpan.FromSeconds(7));

        Assert.False(_host.Instances.ContainsKey(_appName));
        Assert.Empty(Process.GetProcessesByName(_appName));
        Assert.Equal(ApplicationState.Stopped, await ReportedStateAsync());
    }

    /// <summary>
    /// The crash handler runs on Process.Exited's own thread, a few milliseconds after the kill. A
    /// change pushed before it runs races it — the reconcile's stop can then win, mark the exit as
    /// requested, and no retry is ever scheduled, which would make these tests pass for the wrong
    /// reason. Its first visible act is removing the dead instance, so that is what is waited for.
    /// </summary>
    private async Task WaitForCrashHandlerAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (_host!.Instances.ContainsKey(_appName))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the crash handler never removed the dead instance");
            }

            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    private async Task<string> ReportedStateAsync()
    {
        var json = await File.ReadAllTextAsync(_host!.StatusFilePath);
        var snapshot = JsonSerializer.Deserialize<AgentStatusSnapshot>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return Assert.Single(snapshot!.Applications, a => a.Name == _appName).State;
    }

    private string AgentLog() =>
        string.Concat(Directory.GetFiles(Path.Combine(_dataRoot, "Logs"), "agent-*.log").Select(File.ReadAllText));

    private AgentAssignments Assignments(string cron, DesiredState desiredState = DesiredState.Running) => new()
    {
        Applications =
        {
            new ApplicationAssignment
            {
                Name = _appName,
                Path = RepoPaths.SampleServiceDir(),
                DesiredState = desiredState,
                CronOverrides = { ["Sample Job"] = cron },
            },
        },
    };

    private AgentHost CreateHost(IAssignmentSource source, TimeSpan restartDelay)
    {
        var options = new AgentHostOptions
        {
            RestartBaseDelay = restartDelay,
            RestartMaxDelay = restartDelay,
            RestartSettledDuration = TimeSpan.FromSeconds(60),
            MaxRestartAttempts = 5,
        };

        return new AgentHost(source, RepoPaths.RunnerBinDirectory(), Path.Combine(_dataRoot, "Runners"), Path.Combine(_dataRoot, "Logs"), options);
    }

    private async Task<int> WaitForDifferentPidAsync(int previousPid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_host!.Instances.TryGetValue(_appName, out var instance) && instance.Pid != previousPid)
            {
                return instance.Pid;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException($"No new instance appeared within {timeout.TotalSeconds:0}s.");
    }

    /// <summary>An assignment source a test can change under a running host — the one thing StaticAssignmentSource cannot do.</summary>
    private sealed class MutableAssignmentSource : IAssignmentSource
    {
        private readonly SemaphoreSlim _changed = new(0);
        private volatile AgentAssignments _current;

        public MutableAssignmentSource(AgentAssignments initial) => _current = initial;

        public Task<AgentAssignments> GetCurrentAsync(CancellationToken ct) => Task.FromResult(_current);

        public Task WaitForChangeAsync(CancellationToken ct) => _changed.WaitAsync(ct);

        public void Push(AgentAssignments next)
        {
            _current = next;
            _changed.Release();
        }
    }
}
