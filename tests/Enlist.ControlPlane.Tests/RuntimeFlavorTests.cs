using System.Net;
using System.Net.Http.Json;

using Enlist.ControlPlane.Contracts;
using Enlist.TestSupport;

namespace Enlist.ControlPlane.Tests;

/// <summary>API-level tests of RuntimeFlavor — the declaration a policy rule carries so the right agent (see AgentHost's runner-bin selection) can tell whether it has the correct enlist-runner build to host it. See docs/03-architecture/SAD.md §9.</summary>
public sealed class RuntimeFlavorTests : IAsyncLifetime
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

    /// <summary>The self-tag shape {"agent": name} — "target this one agent" collapsed down to its most specific possible tag selector (see ApplicationPolicyDto.TagSelector). These tests only care about RuntimeFlavor CRUD, so a self-tag keeps each rule scoped to one made-up agent name and out of each other's way.</summary>
    private static Dictionary<string, string> SelfTag(string agentName) => new() { ["agent"] = agentName };

    [Fact]
    public async Task A_policy_rule_created_without_specifying_RuntimeFlavor_defaults_to_net10_0()
    {
        var request = new CreateApplicationPolicyRequest(
            ApplicationName: RepoPaths.UniqueAppName(),
            Path: RepoPaths.SampleServiceDir(),
            DesiredState: "Running",
            CronOverrides: null,
            TagSelector: SelfTag("some-agent"),
            PackageDigest: null,
            RuntimeFlavor: null);

        var response = await _client.PostAsJsonAsync("/api/application-policies", request);
        response.EnsureSuccessStatusCode();

        var dto = await response.Content.ReadFromJsonAsync<ApplicationPolicyDto>();
        Assert.Equal(RuntimeFlavors.Default, dto!.RuntimeFlavor);
    }

    [Fact]
    public async Task A_policy_rule_can_be_created_with_an_explicit_legacy_RuntimeFlavor_and_updated_back()
    {
        var request = new CreateApplicationPolicyRequest(
            ApplicationName: RepoPaths.UniqueAppName(),
            Path: RepoPaths.SampleServiceDir(),
            DesiredState: "Running",
            CronOverrides: null,
            TagSelector: SelfTag("some-agent-2"),
            PackageDigest: null,
            RuntimeFlavor: RuntimeFlavors.NetFramework472);

        var created = await _client.PostAsJsonAsync("/api/application-policies", request);
        created.EnsureSuccessStatusCode();
        var createdDto = await created.Content.ReadFromJsonAsync<ApplicationPolicyDto>();
        Assert.Equal(RuntimeFlavors.NetFramework472, createdDto!.RuntimeFlavor);

        // A redeploy that migrates the same policy rule back onto the modern runtime is an update, not
        // a delete-and-recreate — unlike Placement, RuntimeFlavor is expected to be revisable this way.
        var update = new UpdateApplicationPolicyRequest(Path: null, DesiredState: null, CronOverrides: null, PackageDigest: null, RuntimeFlavor: RuntimeFlavors.Default);
        var updated = await _client.PutAsJsonAsync($"/api/application-policies/{createdDto.Id}", update);
        updated.EnsureSuccessStatusCode();
        var updatedDto = await updated.Content.ReadFromJsonAsync<ApplicationPolicyDto>();
        Assert.Equal(RuntimeFlavors.Default, updatedDto!.RuntimeFlavor);
    }

    [Fact]
    public async Task Creating_a_policy_rule_with_an_unrecognized_RuntimeFlavor_is_rejected()
    {
        var request = new CreateApplicationPolicyRequest(
            ApplicationName: RepoPaths.UniqueAppName(),
            Path: RepoPaths.SampleServiceDir(),
            DesiredState: "Running",
            CronOverrides: null,
            TagSelector: SelfTag("some-agent-3"),
            PackageDigest: null,
            RuntimeFlavor: "net48-but-typo'd");

        var response = await _client.PostAsJsonAsync("/api/application-policies", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Updating_a_policy_rule_with_an_unrecognized_RuntimeFlavor_is_rejected()
    {
        var create = new CreateApplicationPolicyRequest(
            ApplicationName: RepoPaths.UniqueAppName(),
            Path: RepoPaths.SampleServiceDir(),
            DesiredState: "Running",
            CronOverrides: null,
            TagSelector: SelfTag("some-agent-4"),
            PackageDigest: null,
            RuntimeFlavor: null);
        var created = await _client.PostAsJsonAsync("/api/application-policies", create);
        created.EnsureSuccessStatusCode();
        var createdDto = await created.Content.ReadFromJsonAsync<ApplicationPolicyDto>();

        var update = new UpdateApplicationPolicyRequest(Path: null, DesiredState: null, CronOverrides: null, PackageDigest: null, RuntimeFlavor: "does-not-exist");
        var response = await _client.PutAsJsonAsync($"/api/application-policies/{createdDto!.Id}", update);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
