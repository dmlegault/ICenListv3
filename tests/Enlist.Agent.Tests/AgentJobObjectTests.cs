using System.Diagnostics;

using Enlist.TestSupport;

namespace Enlist.Agent.Tests;

/// <summary>
/// Proves the actual guarantee, not just that the code compiles: launches the real enlist-agent.exe
/// as its own OS process (not AgentHost in-process — the Job Object protects against exactly the
/// case where nothing managed gets a chance to run any cleanup code), hard-kills it the way a crash
/// or `taskkill /f` (no /t) would, and confirms the runner it supervised dies WITHOUT the agent ever
/// sending it a ShutdownCommand. If the Job Object wiring were missing or broken, this test would see
/// the runner process still alive after the agent is gone.
/// </summary>
public sealed class AgentJobObjectTests : IAsyncLifetime
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "enlist-agent-job-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _appName = RepoPaths.UniqueAppName();
    private Process? _agentProcess;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        try
        {
            if (_agentProcess is { HasExited: false })
            {
                _agentProcess.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }

        // Belt and suspenders: if the guarantee under test somehow failed, don't leak a live process
        // across test runs just because this one test failed its assertion. Scoped to THIS test's own
        // unique app name — other test classes run in parallel and spawn their own like-named
        // processes, which this must never touch.
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

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Killing_the_agent_process_kills_the_runner_it_was_supervising()
    {
        Directory.CreateDirectory(_dataRoot);
        var assignmentsPath = Path.Combine(_dataRoot, "assignments.json");
        var appPath = RepoPaths.SampleServiceDir().Replace("\\", "\\\\");
        await File.WriteAllTextAsync(assignmentsPath, $$"""
        {
          "applications": [
            { "name": "{{_appName}}", "path": "{{appPath}}", "desiredState": "Running" }
          ]
        }
        """);

        var agentDll = Path.Combine(AppContext.BaseDirectory, "enlist-agent.dll");
        Assert.True(File.Exists(agentDll), $"enlist-agent.dll not found at {agentDll} - expected the ProjectReference to Enlist.Agent to copy it there.");

        var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        psi.ArgumentList.Add(agentDll);
        psi.ArgumentList.Add("--runner-bin");
        psi.ArgumentList.Add(RepoPaths.RunnerBinDirectory());
        psi.ArgumentList.Add("--assignments");
        psi.ArgumentList.Add(assignmentsPath);
        psi.ArgumentList.Add("--data");
        psi.ArgumentList.Add(_dataRoot);

        _agentProcess = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start enlist-agent.");

        var runnerProcess = await WaitForProcessAsync(_appName, present: true, TimeSpan.FromSeconds(20));
        Assert.NotNull(runnerProcess);

        // No ShutdownCommand, no Ctrl+C, no cooperative anything — the scenario the Job Object exists for.
        _agentProcess.Kill(entireProcessTree: false);

        await WaitForProcessAsync(_appName, present: false, TimeSpan.FromSeconds(15));
        Assert.Empty(Process.GetProcessesByName(_appName));
    }

    private static async Task<Process?> WaitForProcessAsync(string name, bool present, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var matches = Process.GetProcessesByName(name);
            if (present && matches.Length > 0)
            {
                return matches[0];
            }

            if (!present && matches.Length == 0)
            {
                return null;
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        return present ? null : throw new TimeoutException($"'{name}' was still running after {timeout.TotalSeconds:0}s.");
    }
}
