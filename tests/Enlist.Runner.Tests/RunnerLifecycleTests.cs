using Enlist.Runner.Protocol;

namespace Enlist.Runner.Tests;

/// <summary>
/// Drives a real enlist-runner.exe process, over a real named pipe, against the real
/// Enlist.Sample.Service plugin. This is the standalone test the design doc's sequencing plan calls
/// for before an Enlist.Agent exists at all (docs/03-architecture/enList-v3-Design.md section 11).
/// </summary>
public sealed class RunnerLifecycleTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Discovers_the_sample_services_and_jobs_by_attribute()
    {
        await using var agent = await StubAgent.StartAsync(RepoPaths.SampleServiceOutputDir(), Step);

        var ready = await Receive<ReadyMessage>(agent);

        Assert.Contains(ready.Services, s => s.Name == "Sample Service" && s.TypeName.EndsWith("SampleService"));
        Assert.Contains(ready.Jobs, j => j.Name == "Sample Job" && j.DeclaredCron == "0 0 2 * * ?");
        Assert.Empty(ready.Warnings);

        // The actual pass/fail signal for isolation is DepsFilesFound, not ResolverCount — see
        // IsolationInfo. SampleService has no private dependencies of its own, so 0 resolved privately
        // is healthy, not a failure; a missing deps.json would mean isolation never engaged at all.
        Assert.True(ready.Isolation.DepsFilesFound > 0);
        Assert.Empty(ready.Isolation.OrphanedDeps);

        await agent.Channel.SendAsync(new ShutdownCommand(5000));
        Assert.True(await agent.WaitForExitAsync(Step));
    }

    [Fact]
    public async Task Starting_a_service_runs_it_and_captures_its_console_output()
    {
        await using var agent = await StubAgent.StartAsync(RepoPaths.SampleServiceOutputDir(), Step);
        await Receive<ReadyMessage>(agent);

        await agent.Channel.SendAsync(new StartServiceCommand("Sample Service"));

        await ReceiveUntil<StateChangedMessage>(agent, m => m.Target == "Sample Service" && m.State == RunnerState.Running);
        await ReceiveUntil<LogMessage>(agent, m => m.Source == "Sample Service" && m.Text.StartsWith("heartbeat"));

        await agent.Channel.SendAsync(new StopServiceCommand("Sample Service", TimeoutMs: 5000));
        await ReceiveUntil<StateChangedMessage>(agent, m => m.Target == "Sample Service" && m.State == RunnerState.Stopped);

        await agent.Channel.SendAsync(new ShutdownCommand(5000));
        Assert.True(await agent.WaitForExitAsync(Step));
        Assert.Equal(0, agent.ExitCode);
    }

    [Fact]
    public async Task Running_a_job_reports_success_and_merges_file_settings_with_the_agents_overrides()
    {
        await using var agent = await StubAgent.StartAsync(RepoPaths.SampleServiceOutputDir(), Step);
        await Receive<ReadyMessage>(agent);

        var runId = Guid.NewGuid().ToString("N");
        await agent.Channel.SendAsync(new RunJobCommand("Sample Job", runId, new Dictionary<string, string>
        {
            ["overrideOnly"] = "from-agent",
        }));

        // SampleJob.Run writes one Console.WriteLine per setting — the file-based "greeting" and
        // "retryCount" plus the agent-supplied "overrideOnly" should all show up as captured log lines.
        await ReceiveUntil<LogMessage>(agent, m => m.Source == "Sample Job" && m.Text.Contains("greeting = hello from settings"));
        await ReceiveUntil<LogMessage>(agent, m => m.Source == "Sample Job" && m.Text.Contains("overrideOnly = from-agent"));

        var result = await ReceiveUntil<JobResultMessage>(agent, m => m.Job == "Sample Job" && m.RunId == runId);
        Assert.Equal(JobOutcome.Succeeded, result.Outcome);
        Assert.Null(result.Error);

        await agent.Channel.SendAsync(new ShutdownCommand(5000));
        Assert.True(await agent.WaitForExitAsync(Step));
    }

    [Fact]
    public async Task Closing_the_pipe_without_a_shutdown_command_still_makes_the_runner_exit()
    {
        await using var agent = await StubAgent.StartAsync(RepoPaths.SampleServiceOutputDir(), Step);
        await Receive<ReadyMessage>(agent);

        await agent.Channel.SendAsync(new StartServiceCommand("Sample Service"));
        await ReceiveUntil<StateChangedMessage>(agent, m => m.Target == "Sample Service" && m.State == RunnerState.Running);

        // No ShutdownCommand sent — simulates the agent dying or the pipe otherwise dropping. The
        // runner is expected to stop what it's running and exit on its own rather than hang forever.
        await agent.ClosePipeAsync();

        Assert.True(await agent.WaitForExitAsync(Step));
    }

    [Fact]
    public async Task Lifecycle_methods_declared_on_a_base_class_are_discovered_and_run()
    {
        await using var agent = await StubAgent.StartAsync(RepoPaths.SampleServiceOutputDir(), Step);
        var ready = await Receive<ReadyMessage>(agent);

        // Discovery used to look at declared members only, so this service was reported as having
        // [EnlistService] but no [EnlistStart] — a warning, and a service that could not be started.
        Assert.Contains(ready.Services, s => s.Name == "Inherited Service");
        Assert.Empty(ready.Warnings);

        await agent.Channel.SendAsync(new StartServiceCommand("Inherited Service"));
        await ReceiveUntil<StateChangedMessage>(agent, m => m.Target == "Inherited Service" && m.State == RunnerState.Running);
        await ReceiveUntil<LogMessage>(agent, m => m.Source == "Inherited Service" && m.Text.Contains("from the inherited service"));

        // Stopped before shutdown, as the other lifecycle tests do: a service still logging into a pipe
        // nobody reads parks the runner's outbound flush, and that looks like a runner that will not exit.
        await agent.Channel.SendAsync(new StopServiceCommand("Inherited Service", TimeoutMs: 5000));
        await ReceiveUntil<StateChangedMessage>(agent, m => m.Target == "Inherited Service" && m.State == RunnerState.Stopped);

        await agent.Channel.SendAsync(new ShutdownCommand(5000));
        Assert.True(await agent.WaitForExitAsync(Step));
    }

    [Fact]
    public async Task A_malformed_settings_file_is_said_in_the_plugins_own_log_and_the_run_still_happens()
    {
        // A private copy of the sample with its settings file broken — the deployed one must stay valid.
        var appDir = Path.Combine(Path.GetTempPath(), "enlist-badsettings-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(RepoPaths.SampleServiceOutputDir(), appDir);
        await File.WriteAllTextAsync(Path.Combine(appDir, "Enlist.Sample.Service.settings.json"), "{ \"greeting\": ");

        try
        {
            await using var agent = await StubAgent.StartAsync(appDir, Step);
            await Receive<ReadyMessage>(agent);

            var runId = Guid.NewGuid().ToString("N");
            await agent.Channel.SendAsync(new RunJobCommand("Sample Job", runId, new Dictionary<string, string>()));

            // In the job's own log, as a Warning: a typo in JSON used to read exactly like "no settings".
            var warning = await ReceiveUntil<LogMessage>(agent, m => m.Text.Contains("settings file") && m.Text.Contains("ignored"));
            Assert.Equal(LogLevel.Warning, warning.Level);
            Assert.Equal("Sample Job", warning.Source);

            var result = await ReceiveUntil<JobResultMessage>(agent, m => m.RunId == runId);
            Assert.Equal(JobOutcome.Succeeded, result.Outcome);

            await agent.Channel.SendAsync(new ShutdownCommand(5000));
            Assert.True(await agent.WaitForExitAsync(Step));
        }
        finally
        {
            try
            {
                Directory.Delete(appDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static Task<T> Receive<T>(StubAgent agent) where T : RunnerMessage => agent.NextAsync<T>(Step);

    private static Task<T> ReceiveUntil<T>(StubAgent agent, Func<T, bool> predicate) where T : RunnerMessage => agent.NextMatchingAsync(Step, predicate);
}
