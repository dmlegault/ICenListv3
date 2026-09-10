using System.Net;
using System.Net.Http.Json;

using Enlist.ControlPlane.Contracts;
using Enlist.TestSupport;

namespace Enlist.ControlPlane.Tests;

/// <summary>
/// Validation of the isolation block's own fields, and a guard against the failure mode a regression
/// pass turned up: fields the API accepted, stored and round-tripped while nothing ever acted on them.
/// <see cref="IsolationSpec.Image"/> and <see cref="IsolationSpec.Env"/> were both in that state — the
/// contract documented them, the portal could set them, and the container backend read neither.
/// </summary>
public sealed class IsolationFieldTests : IAsyncLifetime
{
    private ControlPlaneTestServer? _server;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _server = await ControlPlaneTestServer.StartAsync(TimeSpan.FromSeconds(30));
        _client = new HttpClient { BaseAddress = _server.BaseUri };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    private async Task<HttpResponseMessage> CreateAsync(IsolationSpec isolation) =>
        await _client.PostAsJsonAsync("/api/application-policies", new CreateApplicationPolicyRequest(
            ApplicationName: RepoPaths.UniqueAppName(),
            Path: RepoPaths.SampleServiceDir(),
            DesiredState: "Running",
            CronOverrides: null,
            TagSelector: new Dictionary<string, string> { ["agent"] = "iso-agent-" + Guid.NewGuid().ToString("N")[..8] },
            PackageDigest: null,
            RuntimeFlavor: null,
            Isolation: isolation));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public async Task An_out_of_range_container_port_is_rejected(int containerPort)
    {
        var response = await CreateAsync(new IsolationSpec(IsolationModes.Container, Ports: [new PortMapping(containerPort)]));

        // Caught here rather than at `docker run`, whose own complaint is about its command line and
        // names neither the rule nor the application.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("between 1 and 65535", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_out_of_range_host_port_is_rejected_and_the_message_points_at_the_alternative()
    {
        var response = await CreateAsync(new IsolationSpec(IsolationModes.Container, Ports: [new PortMapping(8080, 99999)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The message names the better option rather than only the rule that was broken — leaving the
        // host port unset is the recommended configuration, not merely a legal one.
        Assert.Contains("Omit it entirely", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_unknown_protocol_is_rejected()
    {
        var response = await CreateAsync(new IsolationSpec(IsolationModes.Container, Ports: [new PortMapping(8080, null, "sctp")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("tcp or udp", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Image_and_env_survive_a_round_trip()
    {
        var isolation = new IsolationSpec(
            IsolationModes.Container,
            Image: "enlist/runner:pinned-3.2.0",
            Ports: [new PortMapping(8080, null, "tcp", "http")],
            Networks: ["shared-net"],
            Env: new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Staging" });

        var created = await CreateAsync(isolation);
        created.EnsureSuccessStatusCode();

        var policy = await created.Content.ReadFromJsonAsync<ApplicationPolicyDto>();

        // Round-tripping was never the problem — these stored and returned correctly all along. What
        // was missing is that ContainerRunnerBackend then read Image and Env and did nothing with them.
        // This pins the contract; ContainerRunnerBackend honours it (see its StartAsync).
        Assert.Equal("enlist/runner:pinned-3.2.0", policy!.Isolation!.Image);
        Assert.Equal("Staging", policy.Isolation.Env!["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal("shared-net", Assert.Single(policy.Isolation.Networks!));
        Assert.Equal("http", Assert.Single(policy.Isolation.Ports!).Name);
    }
}
