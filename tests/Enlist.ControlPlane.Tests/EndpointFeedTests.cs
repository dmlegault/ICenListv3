using System.Net.Http.Json;
using System.Text.Json;

using Enlist.ControlPlane.Contracts;
using Enlist.TestSupport;

namespace Enlist.ControlPlane.Tests;

/// <summary>
/// docs/03-architecture/Container-Story.md §8.4 — the level-3 boundary. enList publishes WHERE applications
/// are listening; a reverse proxy decides what to do about it.
///
/// These post status snapshots directly rather than running agents, because what is under test is the
/// projection — that the control plane can read endpoints back out of the opaque snapshot blob, apply
/// staleness, and render a shape Traefik accepts.
/// </summary>
public sealed class EndpointFeedTests : IAsyncLifetime
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

    /// <summary>
    /// Deliberately hand-rolled rather than built from the agent's own AgentStatusSnapshot type: the
    /// control plane does not reference Enlist.Agent, and the whole point of the blob being opaque is
    /// that the two sides are not compile-coupled. Writing the JSON by hand is what actually exercises
    /// the tolerant parse.
    /// </summary>
    private async Task ReportAsync(string agentName, string application, params (string? Name, int ContainerPort, int HostPort)[] endpoints)
    {
        var payload = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            applications = new[]
            {
                new
                {
                    name = application,
                    state = "Running",
                    endpoints = endpoints.Select(e => new
                    {
                        name = e.Name,
                        protocol = "tcp",
                        containerPort = e.ContainerPort,
                        hostPort = e.HostPort,
                        hostAddress = agentName,
                    }).ToArray(),
                },
            },
        };

        var response = await _client.PostAsync(
            $"/api/agents/{agentName}/report",
            new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json"));

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Reported_endpoints_are_projected_into_the_feed()
    {
        var agent = "endpoint-agent-" + Guid.NewGuid().ToString("N")[..8];
        var application = RepoPaths.UniqueAppName();

        await ReportAsync(agent, application, ("http", 8080, 32770));

        var feed = await _client.GetFromJsonAsync<List<EndpointDto>>($"/api/endpoints?application={application}") ?? [];
        var endpoint = Assert.Single(feed);

        Assert.Equal(application, endpoint.ApplicationName);
        Assert.Equal(agent, endpoint.AgentName);
        Assert.Equal("http", endpoint.Name);
        Assert.Equal(8080, endpoint.ContainerPort);
        Assert.Equal(32770, endpoint.HostPort);

        // Posting a report is itself the liveness signal, so an agent that just reported is never stale.
        Assert.False(endpoint.Stale);
    }

    [Fact]
    public async Task An_application_with_no_endpoints_contributes_nothing()
    {
        var agent = "endpoint-agent-" + Guid.NewGuid().ToString("N")[..8];
        var application = RepoPaths.UniqueAppName();

        await ReportAsync(agent, application);

        // The common case — most services and every job expose nothing. The feed is endpoints, not
        // applications, so an application without one simply does not appear.
        Assert.Empty(await _client.GetFromJsonAsync<List<EndpointDto>>($"/api/endpoints?application={application}") ?? []);
    }

    [Fact]
    public async Task The_traefik_projection_emits_services_and_deliberately_no_routers()
    {
        var agent = "endpoint-agent-" + Guid.NewGuid().ToString("N")[..8];
        var application = RepoPaths.UniqueAppName();

        await ReportAsync(agent, application, ("http", 8080, 32771));

        using var doc = JsonDocument.Parse(await _client.GetStringAsync("/api/endpoints/traefik"));
        var http = doc.RootElement.GetProperty("http");

        // Services yes: the live server set is exactly what enList knows and a proxy cannot discover.
        var services = http.GetProperty("services");
        var service = services.EnumerateObject().Single(p => p.Name.Contains(application.ToLowerInvariant()));
        var servers = service.Value.GetProperty("loadBalancer").GetProperty("servers");

        Assert.Equal($"http://{agent}:32771", servers.EnumerateArray().Single().GetProperty("url").GetString());

        // Routers NO. A router is a hostname, a certificate and a path rule — how an organisation wants
        // traffic to arrive. enList inventing those is precisely the line §8.4 refuses to cross, so
        // their absence is a design assertion, not an omission.
        Assert.False(http.TryGetProperty("routers", out _));
    }

    [Fact]
    public async Task A_snapshot_shaped_differently_is_skipped_rather_than_breaking_the_whole_feed()
    {
        var healthyAgent = "endpoint-agent-" + Guid.NewGuid().ToString("N")[..8];
        var oddAgent = "endpoint-agent-" + Guid.NewGuid().ToString("N")[..8];
        var application = RepoPaths.UniqueAppName();

        // An agent from some future (or past) build, reporting a shape this control plane does not know.
        // The snapshot blob is stored unparsed precisely so this is survivable; the feed must degrade to
        // "that agent contributes nothing", never to "the endpoint feed is down".
        (await _client.PostAsync($"/api/agents/{oddAgent}/report",
            new StringContent("""{"somethingElseEntirely":true,"applications":"not-an-array"}""", System.Text.Encoding.UTF8, "application/json")))
            .EnsureSuccessStatusCode();

        await ReportAsync(healthyAgent, application, ("http", 8080, 32772));

        var feed = await _client.GetFromJsonAsync<List<EndpointDto>>($"/api/endpoints?application={application}") ?? [];

        Assert.Equal(healthyAgent, Assert.Single(feed).AgentName);
    }
}
