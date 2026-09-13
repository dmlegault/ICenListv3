using System.Diagnostics;

using Enlist.Agent.Configuration;
using Enlist.ControlPlane.Contracts;
using Enlist.Agent.Supervision;
using Enlist.TestSupport;

namespace Enlist.Agent.Tests;

/// <summary>
/// Phase C2's second exit criterion (docs/03-architecture/Container-Story.md §12): a containerized application must
/// crash-loop and restart <em>exactly like a process</em>.
///
/// This is the test that makes the claim real rather than architectural. AgentHost is handed a
/// ContainerRunnerBackend and otherwise left completely alone — no container-aware configuration, no
/// special casing — and then a container is killed out from under it the way a crash would. If
/// AgentHost had to know it was supervising containers, this test could not be written this way, so it
/// is also the standing check on C0's invariant (§6.4): AgentHost must never branch on backend type.
///
/// Skipped automatically without Docker or the image — see ContainerRunnerBackendTests.
/// </summary>
public sealed class ContainerCrashRestartTests : IAsyncLifetime
{
    private const string Image = Docker.RunnerImage;

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "enlist-container-crash-" + Guid.NewGuid().ToString("N"));
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

    [SkippableFact]
    public async Task A_killed_container_is_restarted_by_the_same_crash_path_a_process_uses()
    {
        Skip.IfNot(await Docker.ImageAvailableAsync(), $"Docker or the {Image} image is unavailable.");

        // Declares CONTAINER isolation on the assignment, exactly as a policy rule would. This
        // is what actually selects the backend, so this also covers the selection path rather than
        // forcing one backend on AgentHost.
        var assignments = new AgentAssignments
        {
            Applications =
            [
                new ApplicationAssignment
                {
                    Name = _appName,
                    Path = RepoPaths.SampleServiceDir(),
                    Isolation = new IsolationSpec(IsolationModes.Container),
                },
            ],
        };

        _host = new AgentHost(
            new StaticAssignmentSource(assignments),
            RepoPaths.RunnerBinDirectory(),
            Path.Combine(_dataRoot, "Runners"),
            Path.Combine(_dataRoot, "Logs"),
            options: AgentHostOptions.Default,
            runnerBackends: new Dictionary<string, IRunnerBackend>
            {
                [IsolationModes.Container] = new ContainerRunnerBackend(
                    new DockerContainerEngine(),
                    Image,
                    TimeSpan.FromSeconds(45),
                    new Enlist.Agent.Logging.AgentFileLogSink(Path.Combine(_dataRoot, "Logs"), null),

                    // Unique per test run: AgentHost reaps containers carrying this label at startup,
                    // and a name shared with a parallel test would reap that test's live containers.
                    agentName: "test-" + Guid.NewGuid().ToString("N")[..8]),
            });

        await _host.StartAsync();

        Assert.True(
            await Poll.TryUntilAsync(() => _host.Instances.ContainsKey(_appName), TimeSpan.FromSeconds(90)),
            "the application never started in a container.");

        var original = _host.Instances[_appName].RuntimeId;
        Assert.False(string.IsNullOrWhiteSpace(original));

        // `docker kill` is the container equivalent of terminating a process without warning: no
        // ShutdownCommand, no cooperative anything. AgentHost must see this as a crash — NOT as a
        // requested stop — and bring the application back on its own.
        await Docker.KillAsync(original);

        Assert.True(
            await Poll.TryUntilAsync(
                () => _host.Instances.TryGetValue(_appName, out var current) && current.RuntimeId != original,
                TimeSpan.FromSeconds(120)),
            "the application was never restarted after its container was killed.");

        Assert.NotEqual(original, _host.Instances[_appName].RuntimeId);
    }
}
