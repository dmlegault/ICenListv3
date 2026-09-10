using System.Diagnostics;
using System.Text.Json;

using Enlist.Agent.Configuration;
using Enlist.Agent.Status;
using Enlist.Runner.Protocol;
using Enlist.TestSupport;

namespace Enlist.Agent.Tests;

/// <summary>
/// Exercises the real AgentHost against a real staged, renamed enlist-runner.exe process running the
/// real Enlist.Sample.Service plugin — same "prove it against the real thing" approach used throughout
/// (Enlist.Runner.Tests), one level up the stack.
/// </summary>
public sealed class AgentHostTests : IAsyncLifetime
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "enlist-agent-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _appName = RepoPaths.UniqueAppName();
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

    private AgentHost CreateHost(ApplicationAssignment app, AgentHostOptions? options = null)
    {
        var assignments = new AgentAssignments { Applications = { app } };
        _host = new AgentHost(assignments, RepoPaths.RunnerBinDirectory(), Path.Combine(_dataRoot, "Runners"), Path.Combine(_dataRoot, "Logs"), options);
        return _host;
    }

    [Fact]
    public async Task Starting_an_application_stages_a_renamed_exe_and_auto_starts_its_services()
    {
        var app = new ApplicationAssignment { Name = _appName, Path = RepoPaths.SampleServiceDir() };
        var host = CreateHost(app);

        var heartbeatSeen = new TaskCompletionSource();
        host.MessageReceived += (_, message) =>
        {
            if (message is LogMessage { Source: "Sample Service" } log && log.Text.StartsWith("heartbeat"))
            {
                heartbeatSeen.TrySetResult();
            }
        };

        await host.StartAsync();

        Assert.True(host.Instances.ContainsKey(_appName));

        // Proves the RunnerStaging trick actually ran, not just that a process started somehow —
        // Task Manager showing "<app>.exe" instead of "enlist-runner.exe" is the whole point.
        var stagedExe = Path.Combine(_dataRoot, "Runners", _appName, _appName + ".exe");
        Assert.True(File.Exists(stagedExe));

        await heartbeatSeen.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task A_cron_override_makes_the_job_fire_on_the_overridden_schedule_instead_of_its_declared_one()
    {
        // SampleJob declares "0 0 2 * * ?" (2am) — if the override below didn't take effect, this
        // test would just time out, which is exactly the failure mode that makes it a real assertion.
        var app = new ApplicationAssignment
        {
            Name = _appName,
            Path = RepoPaths.SampleServiceDir(),
            CronOverrides = { ["Sample Job"] = "*/2 * * * * *" },
        };
        var host = CreateHost(app);

        var jobResult = new TaskCompletionSource<JobResultMessage>();
        host.MessageReceived += (_, message) =>
        {
            if (message is JobResultMessage { Job: "Sample Job" } jr)
            {
                jobResult.TrySetResult(jr);
            }
        };

        await host.StartAsync();

        var result = await jobResult.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(JobOutcome.Succeeded, result.Outcome);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Stopping_the_agent_exits_the_runner_process_and_removes_its_staging_folder()
    {
        var app = new ApplicationAssignment { Name = _appName, Path = RepoPaths.SampleServiceDir() };
        var host = CreateHost(app);
        await host.StartAsync();

        // Captured before StopAsync — AgentHost.StopAsync disposes the runner instance (and its
        // wrapped Process object) as part of stopping, so nothing on `instance` itself is safe to
        // read afterward. The pid is plain data, not a handle, so it survives that disposal fine.
        var instance = host.Instances[_appName];
        var pid = instance.Pid;

        await host.StopAsync();

        // Confirms the OS process is actually gone, from outside the (now-disposed) wrapper.
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));

        var stagingDir = Path.Combine(_dataRoot, "Runners", _appName);
        Assert.False(Directory.Exists(stagingDir));

        _host = null; // already stopped - DisposeAsync's null-host guard covers this, nothing left to do
    }

    [Fact]
    public void AssignmentStore_round_trips_a_real_json_file()
    {
        Directory.CreateDirectory(_dataRoot);
        var path = Path.Combine(_dataRoot, "assignments.json");
        var appPath = RepoPaths.SampleServiceDir().Replace("\\", "\\\\");
        File.WriteAllText(path, $$"""
        {
          "applications": [
            {
              "name": "SampleService",
              "path": "{{appPath}}",
              "desiredState": "Running",
              "cronOverrides": { "Sample Job": "*/5 * * * * *" }
            }
          ]
        }
        """);

        var assignments = AssignmentStore.Load(path);

        var app = Assert.Single(assignments.Applications);
        Assert.Equal("SampleService", app.Name);
        Assert.Equal(DesiredState.Running, app.DesiredState);
        Assert.Equal("*/5 * * * * *", app.CronOverrides["Sample Job"]);
    }

    [Fact]
    public async Task Status_snapshot_reflects_the_running_service_and_a_completed_job_run()
    {
        var app = new ApplicationAssignment
        {
            Name = _appName,
            Path = RepoPaths.SampleServiceDir(),
            CronOverrides = { ["Sample Job"] = "*/2 * * * * *" },
        };
        var host = CreateHost(app);

        var jobResult = new TaskCompletionSource<JobResultMessage>();
        host.MessageReceived += (_, message) =>
        {
            if (message is JobResultMessage { Job: "Sample Job" } jr)
            {
                jobResult.TrySetResult(jr);
            }
        };

        await host.StartAsync();
        await jobResult.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // HandleMessageAsync writes the snapshot synchronously within its own await chain (see
        // AgentHost), so by the time jobResult's continuation runs the write has already been
        // awaited — no arbitrary sleep needed here to avoid a race with the write itself.
        var json = await File.ReadAllTextAsync(host.StatusFilePath);
        var snapshot = JsonSerializer.Deserialize<AgentStatusSnapshot>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var appStatus = Assert.Single(snapshot!.Applications);
        Assert.Equal(_appName, appStatus.Name);
        Assert.Equal(ApplicationState.Running, appStatus.State);
        Assert.NotNull(appStatus.Pid);
        Assert.Contains(appStatus.Services, s => s.Name == "Sample Service" && s.State == "Running");
        Assert.Contains(appStatus.Jobs, j => j.Name == "Sample Job" && j.LastRunOutcome == JobRunOutcome.Succeeded);
    }

    /// <summary>
    /// A run an operator stopped is not a failure, and must not be reported back to them as one. This
    /// pins both places it would previously have said otherwise: the status snapshot the portal reads,
    /// and the log line an operator reads, which used to say FAILED at Error level.
    /// </summary>
    [Fact]
    public async Task A_run_stopped_by_a_command_is_recorded_as_Cancelled_rather_than_Failed()
    {
        // Ledger Rebuild takes ~10s in chunks, so unlike the one-shot Sample Job there is genuinely a
        // run in flight to catch. Stop also unregisters the job, so nothing fires again after this one.
        var app = new ApplicationAssignment
        {
            Name = _appName,
            Path = RepoPaths.OrderProcessorDir(),
            CronOverrides = { ["Ledger Rebuild"] = "*/2 * * * * *" },
        };
        var host = CreateHost(app);

        var underway = new TaskCompletionSource();
        var jobResult = new TaskCompletionSource<JobResultMessage>();
        host.MessageReceived += (_, message) =>
        {
            if (message is LogMessage { Source: "Ledger Rebuild" } log && log.Text.Contains("chunk 1/20"))
            {
                underway.TrySetResult();
            }

            if (message is JobResultMessage { Job: "Ledger Rebuild" } jr)
            {
                jobResult.TrySetResult(jr);
            }
        };

        await host.StartAsync();
        await underway.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The same path the portal's Disable button takes.
        await host.ExecuteCommandAsync(_appName, "Job", "Ledger Rebuild", "Stop");

        var result = await jobResult.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(JobOutcome.Cancelled, result.Outcome);

        var json = await File.ReadAllTextAsync(host.StatusFilePath);
        var snapshot = JsonSerializer.Deserialize<AgentStatusSnapshot>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var appStatus = Assert.Single(snapshot!.Applications);
        Assert.Contains(appStatus.Jobs, j => j.Name == "Ledger Rebuild" && j.LastRunOutcome == JobRunOutcome.Cancelled);

        var appLog = string.Concat(Directory.GetFiles(Path.Combine(_dataRoot, "Logs", _appName), "*.log").Select(File.ReadAllText));
        Assert.Contains("cancelled after", appLog);
        Assert.DoesNotContain("FAILED", appLog);
    }

    [Fact]
    public async Task The_periodic_retention_sweep_prunes_old_log_files_while_the_agent_is_running()
    {
        var app = new ApplicationAssignment { Name = _appName, Path = RepoPaths.SampleServiceDir() };
        var options = new AgentHostOptions
        {
            LogRetention = TimeSpan.FromDays(1),
            LogRetentionSweepInterval = TimeSpan.FromMilliseconds(300),
        };
        var host = CreateHost(app, options);

        // Pre-age a log file before the agent even gets a chance to sweep, so the very first sweep
        // (which StartAsync kicks off immediately) has something real to prune.
        var oldLogDir = Path.Combine(_dataRoot, "Logs", "SomeOldApp");
        Directory.CreateDirectory(oldLogDir);
        var oldLogFile = Path.Combine(oldLogDir, "2020-01-01.log");
        await File.WriteAllTextAsync(oldLogFile, "stale");
        File.SetLastWriteTimeUtc(oldLogFile, DateTime.UtcNow - TimeSpan.FromDays(10));

        await host.StartAsync();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (File.Exists(oldLogFile) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.False(File.Exists(oldLogFile));
    }
}
