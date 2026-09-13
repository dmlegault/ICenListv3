using Enlist.ControlPlane.Contracts;
using System.Diagnostics;

using Enlist.Agent.Logging;
using Enlist.Agent.Supervision;
using Enlist.Runner.Protocol;
using Enlist.TestSupport;

namespace Enlist.Agent.Tests;

/// <summary>
/// docs/03-architecture/Container-Story.md §12 — UC-1 — an existing package runs containerized with no
/// rebuild.
///
/// These drive real Docker, the real enlist/runner image, and the real SampleService package through
/// the real <see cref="ContainerRunnerBackend"/>. Nothing is stubbed, in keeping with how every other
/// layer in this project is tested (StubAgent drives a real runner; ControlPlaneTestServer spawns a
/// real control plane).
///
/// SKIPPED AUTOMATICALLY when Docker or the image is unavailable, rather than failing: the rest of the
/// suite must stay runnable on a machine with no container engine, and a red test there would say
/// "enList is broken" when it means "Docker isn't installed". Build the image first with:
///
///   docker build -f src/Enlist.Runner/Dockerfile -t enlist/runner:dev .
/// </summary>
public sealed class ContainerRunnerBackendTests : IAsyncLifetime
{
    private const string Image = Docker.RunnerImage;

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "enlist-container-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(60);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch
        {
        }

        return Task.CompletedTask;
    }

    [SkippableFact]
    public async Task An_unmodified_package_runs_in_a_container_and_completes_discovery()
    {
        Skip.IfNot(await Docker.ImageAvailableAsync(), $"Docker or the {Image} image is unavailable.");

        await using var instance = await StartAsync();

        var ready = await instance.WhenReady.WaitAsync(Step);

        // The identical assertions the process-backend tests make. "No rebuild" is the whole claim of
        // UC-1, so the containerized runner must discover exactly what the process one discovers from
        // exactly the same package directory on disk.
        Assert.Contains(ready.Services, s => s.Name == "Sample Service");
        Assert.Contains(ready.Jobs, j => j.Name == "Sample Job");
        Assert.Equal(RunnerProtocol.Version, ready.ProtocolVersion);

        // Isolation must engage inside the container too — it is a property of how the runner loads
        // assemblies, not of where it runs, and a silently-disengaged isolation looks identical to a
        // working one until two plugin versions collide (see IsolationInfo).
        Assert.True(ready.Isolation.DepsFilesFound > 0);
    }

    [SkippableFact]
    public async Task A_container_instance_identifies_itself_by_container_id_not_by_pid()
    {
        Skip.IfNot(await Docker.ImageAvailableAsync(), $"Docker or the {Image} image is unavailable.");

        await using var instance = await StartAsync();
        await instance.WhenReady.WaitAsync(Step);

        // Pid is deliberately 0 rather than a plausible-looking number: on Docker Desktop a container's
        // "host pid" lives inside a Linux VM and means nothing to anything on the Windows host.
        Assert.Equal(0, instance.Pid);

        // RuntimeId is the identity that IS actionable — this is what an operator pastes into
        // `docker logs`.
        Assert.False(string.IsNullOrWhiteSpace(instance.RuntimeId));
        Assert.False(instance.HasExited);
    }

    [SkippableFact]
    public async Task Commands_reach_a_service_inside_the_container()
    {
        Skip.IfNot(await Docker.ImageAvailableAsync(), $"Docker or the {Image} image is unavailable.");

        var states = new List<StateChangedMessage>();
        await using var instance = await StartAsync((_, message) =>
        {
            if (message is StateChangedMessage state)
            {
                lock (states)
                {
                    states.Add(state);
                }
            }

            return Task.CompletedTask;
        });

        await instance.WhenReady.WaitAsync(Step);
        await instance.SendAsync(new StartServiceCommand("Sample Service"));

        // Proves the channel carries commands INTO the container and state back OUT — not merely that
        // the handshake succeeded.
        var deadline = DateTime.UtcNow + Step;
        while (DateTime.UtcNow < deadline)
        {
            lock (states)
            {
                if (states.Any(s => s.Target == "Sample Service" && s.State == RunnerState.Running))
                {
                    return;
                }
            }

            await Task.Delay(200);
        }

        Assert.Fail("the service never reported Running inside the container.");
    }

    [SkippableFact]
    public async Task Stopping_a_container_instance_shuts_it_down_gracefully()
    {
        Skip.IfNot(await Docker.ImageAvailableAsync(), $"Docker or the {Image} image is unavailable.");

        await using var instance = await StartAsync();
        await instance.WhenReady.WaitAsync(Step);

        // True means the container left on its own after ShutdownCommand rather than being SIGKILLed —
        // i.e. the command actually arrived and the runner acted on it. Same meaning as the process
        // backend's return, which is the point: AgentHost cannot tell the two apart.
        Assert.True(await instance.StopAsync(TimeSpan.FromSeconds(20)), "the container had to be killed rather than shutting down gracefully.");
    }

    private async Task<IRunnerInstance> StartAsync(Func<IRunnerInstance, RunnerMessage, Task>? onMessage = null)
    {
        var logRoot = Path.Combine(_dataRoot, "Logs");
        Directory.CreateDirectory(logRoot);

        var backend = new ContainerRunnerBackend(
            new DockerContainerEngine(),
            Image,
            TimeSpan.FromSeconds(45),
            new AgentFileLogSink(logRoot, null),

            // A unique owner label per test class instance. These classes run in parallel and the
            // orphan reap deletes by this label, so a shared name would let one test destroy another
            // test's live containers.
            agentName: "test-" + Guid.NewGuid().ToString("N")[..8]);

        // runnerBinDirectory is ignored by this backend — the runner lives in the image, so there is
        // nothing to stage.
        return await backend.StartAsync(new RunnerStartRequest(
            RepoPaths.UniqueAppName(),
            RunnerBinDirectory: "",
            RepoPaths.SampleServiceDir(),
            IsolationSpec.ProcessDefault,
            onMessage ?? ((_, _) => Task.CompletedTask)));
    }
}
