using Enlist.ControlPlane.Contracts;
using System.Diagnostics;

using Enlist.Agent.Logging;
using Enlist.Agent.Supervision;
using Enlist.TestSupport;

namespace Enlist.Agent.Tests;

/// <summary>
/// A container outlives the agent that started it. That is the opposite of the process backend, where a
/// Windows Job Object guarantees an abnormally-dead agent takes its runners with it — so containers are
/// WEAKER here, not stronger, and need an explicit cleanup the process path never did.
///
/// Without this, every hard agent restart leaks a container: observed directly on the dev box, where a
/// stopped `enlist-OrderProcessor-…` sat around after the agent was killed mid-session.
///
/// Skipped automatically without Docker or the image — see ContainerRunnerBackendTests.
/// </summary>
public sealed class ContainerOrphanReapTests : IAsyncLifetime
{
    private const string Image = Docker.RunnerImage;

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "enlist-reap-tests-" + Guid.NewGuid().ToString("N"));

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
    public async Task A_container_left_by_a_previous_run_is_removed_at_startup()
    {
        Skip.IfNot(await Docker.ImageAvailableAsync(), $"Docker or the {Image} image is unavailable.");

        var agentName = "reap-" + Guid.NewGuid().ToString("N")[..8];
        var backend = CreateBackend(agentName);

        // Simulates the agent being killed rather than stopped: the container is started and then simply
        // abandoned, with no DisposeAsync ever running — exactly what a Ctrl+C or a crash leaves behind.
        var instance = await backend.StartAsync(new RunnerStartRequest(
            RepoPaths.UniqueAppName(), "", RepoPaths.SampleServiceDir(), IsolationSpec.ProcessDefault, (_, _) => Task.CompletedTask));
        var abandonedId = instance.RuntimeId;

        Assert.True(await Docker.ContainerExistsAsync(abandonedId), "the container under test was never created.");

        // A fresh backend with the SAME agent name — i.e. the same agent starting up again.
        await CreateBackend(agentName).ReapOrphansAsync();

        Assert.False(await Docker.ContainerExistsAsync(abandonedId), "the orphaned container survived the startup reap.");
    }

    [SkippableFact]
    public async Task Reaping_one_agent_does_not_touch_another_agents_containers()
    {
        Skip.IfNot(await Docker.ImageAvailableAsync(), $"Docker or the {Image} image is unavailable.");

        // The property that makes reaping safe at all. Several agents routinely share one container
        // engine — two do on this project's own dev box — so a sweep based on the "enlist-" name prefix
        // instead of the ownership label would destroy a running application belonging to a different,
        // perfectly healthy agent.
        var otherAgent = "reap-other-" + Guid.NewGuid().ToString("N")[..8];
        var otherBackend = CreateBackend(otherAgent);

        var live = await otherBackend.StartAsync(new RunnerStartRequest(
            RepoPaths.UniqueAppName(), "", RepoPaths.SampleServiceDir(), IsolationSpec.ProcessDefault, (_, _) => Task.CompletedTask));
        await using (live)
        {
            var liveId = live.RuntimeId;

            // A DIFFERENT agent starts up and reaps. It must leave the container above completely alone.
            await CreateBackend("reap-self-" + Guid.NewGuid().ToString("N")[..8]).ReapOrphansAsync();

            Assert.True(await Docker.ContainerExistsAsync(liveId), "another agent's reap destroyed a live container that did not belong to it.");
        }
    }

    private ContainerRunnerBackend CreateBackend(string agentName)
    {
        var logRoot = Path.Combine(_dataRoot, "Logs");
        Directory.CreateDirectory(logRoot);

        return new ContainerRunnerBackend(
            new DockerContainerEngine(),
            Image,
            TimeSpan.FromSeconds(45),
            new AgentFileLogSink(logRoot, null),
            agentName);
    }
}
