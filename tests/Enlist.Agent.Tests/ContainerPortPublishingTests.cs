using System.Diagnostics;
using System.Net.Sockets;

using Enlist.Agent.Logging;
using Enlist.Agent.Supervision;
using Enlist.ControlPlane.Contracts;
using Enlist.TestSupport;

namespace Enlist.Agent.Tests;

/// <summary>
/// docs/03-architecture/Container-Story.md §8.1, level 1 — an application's own ports are declared on the
/// policy, published by the agent, and reported back as resolved endpoints.
///
/// The distinction these tests exist to pin down is DECLARED versus RESOLVED. A static mapping is known
/// in advance; a dynamic one (null HostPort) is not knowable until the engine has allocated it, which is
/// precisely why the status snapshot carries resolved endpoints separately from the PortMapping that
/// asked for them.
///
/// Skipped automatically without Docker or the image — see ContainerRunnerBackendTests.
/// </summary>
public sealed class ContainerPortPublishingTests : IAsyncLifetime
{
    private const string Image = Docker.RunnerImage;

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "enlist-port-tests-" + Guid.NewGuid().ToString("N"));

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
    public async Task A_dynamic_port_is_allocated_by_the_engine_and_reported_back()
    {
        Skip.IfNot(await Docker.ImageAvailableAsync(), $"Docker or the {Image} image is unavailable.");

        // HostPort null = "allocate one". This is what makes a tag-selector rule work across many
        // agents: a single hard-coded port could not serve them all.
        var isolation = new IsolationSpec(IsolationModes.Container, Ports: [new PortMapping(8080, HostPort: null, "tcp", "http")]);

        await using var instance = await StartAsync(isolation);
        await instance.WhenReady.WaitAsync(TimeSpan.FromSeconds(60));

        var endpoint = Assert.Single(instance.Endpoints);

        Assert.Equal(8080, endpoint.ContainerPort);
        Assert.Equal("http", endpoint.Name);
        Assert.Equal("tcp", endpoint.Protocol);

        // The whole point: a real, engine-chosen port that could not have been predicted from the policy.
        Assert.InRange(endpoint.HostPort, 1, 65535);
        Assert.False(string.IsNullOrWhiteSpace(endpoint.HostAddress));
    }

    [SkippableFact]
    public async Task A_static_port_is_published_on_exactly_the_port_that_was_asked_for()
    {
        Skip.IfNot(await Docker.ImageAvailableAsync(), $"Docker or the {Image} image is unavailable.");

        // A free port is picked here rather than hard-coded, so the test does not fail on a developer
        // machine that happens to be using whatever number was chosen.
        var requested = FreeTcpPort();
        var isolation = new IsolationSpec(IsolationModes.Container, Ports: [new PortMapping(8080, requested, "tcp", "http")]);

        await using var instance = await StartAsync(isolation);
        await instance.WhenReady.WaitAsync(TimeSpan.FromSeconds(60));

        var endpoint = Assert.Single(instance.Endpoints);

        // Static means static: something outside enList may already be pointed at this number, which is
        // the entire reason for supporting it over allocation.
        Assert.Equal(requested, endpoint.HostPort);
        Assert.Equal(8080, endpoint.ContainerPort);
    }

    [SkippableFact]
    public async Task Environment_variables_declared_on_the_policy_reach_the_container()
    {
        Skip.IfNot(await Docker.ImageAvailableAsync(), $"Docker or the {Image} image is unavailable.");

        // Env was accepted, stored and round-tripped by the API while ContainerRunnerBackend read it and
        // did nothing — a field that looked supported and was not. Asserting the container actually
        // receives it is the only way that stays fixed.
        var isolation = new IsolationSpec(
            IsolationModes.Container,
            Env: new Dictionary<string, string> { ["ENLIST_REGRESSION_MARKER"] = "present" });

        await using var instance = await StartAsync(isolation);
        await instance.WhenReady.WaitAsync(TimeSpan.FromSeconds(60));

        var env = await Docker.CaptureAsync("inspect", "--format", "{{json .Config.Env}}", instance.RuntimeId);
        Assert.Contains("ENLIST_REGRESSION_MARKER=present", env);
    }

    [SkippableFact]
    public async Task An_image_override_on_the_policy_beats_the_agents_default()
    {
        Skip.IfNot(await Docker.ImageAvailableAsync(), $"Docker or the {Image} image is unavailable.");

        // Same class of bug: IsolationSpec.Image existed, was documented, and was ignored in favour of
        // the agent-wide image. Its point is pinning ONE application to a specific enList release during
        // a migration without reconfiguring the agent.
        var tagged = "enlist/runner:regression-alias";
        await Docker.CaptureAsync("tag", Image, tagged);

        await using var instance = await StartAsync(new IsolationSpec(IsolationModes.Container, Image: tagged));
        await instance.WhenReady.WaitAsync(TimeSpan.FromSeconds(60));

        var image = await Docker.CaptureAsync("inspect", "--format", "{{.Config.Image}}", instance.RuntimeId);
        Assert.Contains("regression-alias", image);
    }

    [SkippableFact]
    public async Task An_application_declaring_no_ports_reports_no_endpoints()
    {
        Skip.IfNot(await Docker.ImageAvailableAsync(), $"Docker or the {Image} image is unavailable.");

        await using var instance = await StartAsync(new IsolationSpec(IsolationModes.Container));
        await instance.WhenReady.WaitAsync(TimeSpan.FromSeconds(60));

        // The common case by far — most services and every job expose nothing. Empty, never null, so
        // the portal renders "—" rather than having to guard.
        Assert.Empty(instance.Endpoints);
    }

    private async Task<IRunnerInstance> StartAsync(IsolationSpec isolation)
    {
        var logRoot = Path.Combine(_dataRoot, "Logs");
        Directory.CreateDirectory(logRoot);

        var backend = new ContainerRunnerBackend(
            new DockerContainerEngine(),
            Image,
            TimeSpan.FromSeconds(45),
            new AgentFileLogSink(logRoot, null),
            agentName: "port-" + Guid.NewGuid().ToString("N")[..8]);

        return await backend.StartAsync(new RunnerStartRequest(
            RepoPaths.UniqueAppName(), "", RepoPaths.SampleServiceDir(), isolation, (_, _) => Task.CompletedTask));
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
