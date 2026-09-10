using System.Net.Http.Json;

using Enlist.ControlPlane.Contracts;
using Enlist.TestSupport;

namespace Enlist.ControlPlane.Tests;

/// <summary>
/// docs/03-architecture/Container-Story.md §8.2 — the second conflict class.
///
/// The pre-existing conflict asks "do two rules for the SAME application disagree about what to run?"
/// and is resolved per application group. A static host port collides differently — two rules for
/// DIFFERENT applications, each perfectly self-consistent, both demanding the same host port on the
/// same agent. Nothing in the per-application check can see it, because it lives between groups.
///
/// Caught at resolution rather than at container start, because the engine's own failure ("port is
/// already allocated") names nothing an operator can act on: not the other application, not the rule,
/// not the agent.
/// </summary>
public sealed class PortCollisionTests : IAsyncLifetime
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

    private static Dictionary<string, string> SelfTag(string agentName) => new() { ["agent"] = agentName };

    private async Task CreateAsync(string application, string agent, IsolationSpec isolation) =>
        (await _client.PostAsJsonAsync("/api/application-policies", new CreateApplicationPolicyRequest(
            ApplicationName: application,
            Path: RepoPaths.SampleServiceDir(),
            DesiredState: "Running",
            CronOverrides: null,
            TagSelector: SelfTag(agent),
            PackageDigest: null,
            RuntimeFlavor: null,
            Isolation: isolation))).EnsureSuccessStatusCode();

    private async Task<List<ApplicationPolicyDto>> ResolveForAsync(string agent) =>
        await _client.GetFromJsonAsync<List<ApplicationPolicyDto>>($"/api/agents/{agent}/policies") ?? [];

    [Fact]
    public async Task Two_applications_claiming_the_same_static_host_port_on_one_agent_both_report_a_conflict()
    {
        var agent = "port-agent-" + Guid.NewGuid().ToString("N")[..8];
        var first = RepoPaths.UniqueAppName();
        var second = RepoPaths.UniqueAppName();

        await CreateAsync(first, agent, new IsolationSpec(IsolationModes.Container, Ports: [new PortMapping(8080, 9100)]));
        await CreateAsync(second, agent, new IsolationSpec(IsolationModes.Container, Ports: [new PortMapping(8080, 9100)]));

        var resolved = await ResolveForAsync(agent);

        // BOTH sides are flagged, deliberately. There is no principled way to pick a winner, and
        // silently starting whichever got there first would leave the other failing at the engine with
        // an error that names nothing.
        foreach (var name in new[] { first, second })
        {
            var rule = Assert.Single(resolved, r => r.ApplicationName == name);
            Assert.NotNull(rule.ConflictReason);
            Assert.Contains("9100", rule.ConflictReason);
        }

        // Each side names the OTHER, so the message alone is enough to go and fix it.
        Assert.Contains(second, Assert.Single(resolved, r => r.ApplicationName == first).ConflictReason);
        Assert.Contains(first, Assert.Single(resolved, r => r.ApplicationName == second).ConflictReason);
    }

    [Fact]
    public async Task Dynamic_ports_never_collide()
    {
        var agent = "port-agent-" + Guid.NewGuid().ToString("N")[..8];

        // Null HostPort means "let the engine allocate", which is exactly the mechanism that lets one
        // tag-selector rule fan out across many agents. Two of them are not a conflict and must never be
        // reported as one — otherwise the recommended configuration would be the one that breaks.
        await CreateAsync(RepoPaths.UniqueAppName(), agent, new IsolationSpec(IsolationModes.Container, Ports: [new PortMapping(8080)]));
        await CreateAsync(RepoPaths.UniqueAppName(), agent, new IsolationSpec(IsolationModes.Container, Ports: [new PortMapping(8080)]));

        Assert.All(await ResolveForAsync(agent), r => Assert.Null(r.ConflictReason));
    }

    [Fact]
    public async Task The_same_static_port_on_two_different_agents_is_not_a_collision()
    {
        var portInBoth = 9200;
        var agentA = "port-agent-" + Guid.NewGuid().ToString("N")[..8];
        var agentB = "port-agent-" + Guid.NewGuid().ToString("N")[..8];

        await CreateAsync(RepoPaths.UniqueAppName(), agentA, new IsolationSpec(IsolationModes.Container, Ports: [new PortMapping(8080, portInBoth)]));
        await CreateAsync(RepoPaths.UniqueAppName(), agentB, new IsolationSpec(IsolationModes.Container, Ports: [new PortMapping(8080, portInBoth)]));

        // A host port is only scarce within one host. Reporting this as a conflict would make static
        // ports unusable for their main purpose — the same service on the same port across a fleet.
        Assert.All(await ResolveForAsync(agentA), r => Assert.Null(r.ConflictReason));
        Assert.All(await ResolveForAsync(agentB), r => Assert.Null(r.ConflictReason));
    }

    [Fact]
    public async Task Different_static_ports_on_one_agent_are_not_a_collision()
    {
        var agent = "port-agent-" + Guid.NewGuid().ToString("N")[..8];

        await CreateAsync(RepoPaths.UniqueAppName(), agent, new IsolationSpec(IsolationModes.Container, Ports: [new PortMapping(8080, 9300)]));
        await CreateAsync(RepoPaths.UniqueAppName(), agent, new IsolationSpec(IsolationModes.Container, Ports: [new PortMapping(8080, 9301)]));

        // Same CONTAINER port, different HOST ports — the ordinary way to run two of something on one
        // box. Only the host side is scarce.
        Assert.All(await ResolveForAsync(agent), r => Assert.Null(r.ConflictReason));
    }
}
