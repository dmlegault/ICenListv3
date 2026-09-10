using System.Net;
using System.Net.Http.Json;

using Enlist.ControlPlane.Contracts;
using Enlist.TestSupport;

namespace Enlist.ControlPlane.Tests;

/// <summary>API-level tests of the agent registry and tag-selector placement — including self-tag targeting and conflict detection — against the real control plane. No agent process involved, just the mechanism itself.</summary>
public sealed class AgentRegistryTests : IAsyncLifetime
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

    [Fact]
    public async Task An_agent_is_registered_automatically_the_first_time_it_fetches_its_policies()
    {
        var agentName = "auto-" + Guid.NewGuid().ToString("N")[..8];

        var before = await _client.GetAsync($"/api/agents/{agentName}");
        Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);

        await _client.GetAsync($"/api/agents/{agentName}/policies");

        var agent = await _client.GetFromJsonAsync<AgentDto>($"/api/agents/{agentName}");
        Assert.NotNull(agent);
        Assert.Equal(agentName, agent!.Name);
        Assert.Empty(agent.Tags);
    }

    [Fact]
    public async Task Setting_tags_before_an_agent_has_ever_connected_pre_registers_it()
    {
        var agentName = "preseed-" + Guid.NewGuid().ToString("N")[..8];
        var tags = new Dictionary<string, string> { ["role"] = "web", ["env"] = "prod" };

        var response = await _client.PutAsJsonAsync($"/api/agents/{agentName}/tags", new SetAgentTagsRequest(tags));
        response.EnsureSuccessStatusCode();

        var agent = await _client.GetFromJsonAsync<AgentDto>($"/api/agents/{agentName}");
        Assert.Equal("web", agent!.Tags["role"]);
        Assert.Equal("prod", agent.Tags["env"]);
    }

    [Fact]
    public async Task A_policy_rule_targeting_a_tag_selector_is_returned_only_to_matching_agents()
    {
        var matchingAgent = "match-" + Guid.NewGuid().ToString("N")[..8];
        var nonMatchingAgent = "nomatch-" + Guid.NewGuid().ToString("N")[..8];

        await _client.PutAsJsonAsync($"/api/agents/{matchingAgent}/tags", new SetAgentTagsRequest(new Dictionary<string, string> { ["role"] = "web" }));
        await _client.PutAsJsonAsync($"/api/agents/{nonMatchingAgent}/tags", new SetAgentTagsRequest(new Dictionary<string, string> { ["role"] = "batch" }));

        var request = new CreateApplicationPolicyRequest(
            ApplicationName: RepoPaths.UniqueAppName(),
            Path: RepoPaths.SampleServiceDir(),
            DesiredState: "Running",
            CronOverrides: null,
            TagSelector: new Dictionary<string, string> { ["role"] = "web" });
        var created = await _client.PostAsJsonAsync("/api/application-policies", request);
        created.EnsureSuccessStatusCode();

        var matching = await _client.GetFromJsonAsync<List<ApplicationPolicyDto>>($"/api/agents/{matchingAgent}/policies");
        var nonMatching = await _client.GetFromJsonAsync<List<ApplicationPolicyDto>>($"/api/agents/{nonMatchingAgent}/policies");

        Assert.Single(matching!);
        Assert.Empty(nonMatching!);
    }

    [Fact]
    public async Task An_agents_self_tag_uniquely_matches_a_policy_rule_targeting_it_by_name()
    {
        var targetAgent = "target-" + Guid.NewGuid().ToString("N")[..8];
        var otherAgent = "other-" + Guid.NewGuid().ToString("N")[..8];

        // {"agent": name} is what "target this one agent" collapses down to — the most specific
        // possible tag selector, matched against the implicit self-tag every agent carries without it
        // ever being stored (see EffectiveAgentTags in Program.cs).
        var request = new CreateApplicationPolicyRequest(
            ApplicationName: RepoPaths.UniqueAppName(),
            Path: RepoPaths.SampleServiceDir(),
            DesiredState: "Running",
            CronOverrides: null,
            TagSelector: new Dictionary<string, string> { ["agent"] = targetAgent });
        (await _client.PostAsJsonAsync("/api/application-policies", request)).EnsureSuccessStatusCode();

        var matching = await _client.GetFromJsonAsync<List<ApplicationPolicyDto>>($"/api/agents/{targetAgent}/policies");
        var nonMatching = await _client.GetFromJsonAsync<List<ApplicationPolicyDto>>($"/api/agents/{otherAgent}/policies");

        Assert.Single(matching!);
        Assert.Empty(nonMatching!);
    }

    [Fact]
    public async Task Two_agreeing_policy_rules_matching_the_same_agent_resolve_cleanly_with_no_conflict()
    {
        var agentName = "agree-" + Guid.NewGuid().ToString("N")[..8];
        var appName = RepoPaths.UniqueAppName();

        // A self-tag rule and a broader tag rule that happen to reference the exact same build and
        // desired state right now — the common case (e.g. a canary rule and a fallback rule agreeing).
        var selfTagRequest = new CreateApplicationPolicyRequest(
            ApplicationName: appName,
            Path: RepoPaths.SampleServiceDir(),
            DesiredState: "Running",
            CronOverrides: null,
            TagSelector: new Dictionary<string, string> { ["agent"] = agentName });
        (await _client.PostAsJsonAsync("/api/application-policies", selfTagRequest)).EnsureSuccessStatusCode();

        var broadRequest = new CreateApplicationPolicyRequest(
            ApplicationName: appName,
            Path: RepoPaths.SampleServiceDir(),
            DesiredState: "Running",
            CronOverrides: null,
            TagSelector: new Dictionary<string, string> { ["env"] = "agree-test" });
        (await _client.PostAsJsonAsync("/api/application-policies", broadRequest)).EnsureSuccessStatusCode();

        // The agent doesn't match the broad selector yet — retagging it now makes BOTH rules match.
        (await _client.PutAsJsonAsync($"/api/agents/{agentName}/tags", new SetAgentTagsRequest(new Dictionary<string, string> { ["env"] = "agree-test" })))
            .EnsureSuccessStatusCode();

        var resolved = await _client.GetFromJsonAsync<List<ApplicationPolicyDto>>($"/api/agents/{agentName}/policies");
        var entry = Assert.Single(resolved!);
        Assert.Equal(appName, entry.ApplicationName);
        Assert.Null(entry.ConflictReason);

        // The portal-facing endpoint deliberately does NOT collapse this the same way — both raw rules
        // stay visible even though they agree, so a human can see everything actually matching.
        var portalView = await _client.GetFromJsonAsync<List<ApplicationPolicyDto>>($"/api/application-policies?agentName={agentName}");
        Assert.Equal(2, portalView!.Count);
    }

    [Fact]
    public async Task Two_disagreeing_policy_rules_matching_the_same_agent_produce_a_conflict_instead_of_an_arbitrary_pick()
    {
        var agentName = "conflict-" + Guid.NewGuid().ToString("N")[..8];
        var appName = RepoPaths.UniqueAppName();

        var selfTagRequest = new CreateApplicationPolicyRequest(
            ApplicationName: appName,
            Path: RepoPaths.SampleServiceDir(),
            DesiredState: "Running",
            CronOverrides: null,
            TagSelector: new Dictionary<string, string> { ["agent"] = agentName });
        (await _client.PostAsJsonAsync("/api/application-policies", selfTagRequest)).EnsureSuccessStatusCode();

        // Disagrees on DesiredState AND source — there is no single correct answer to hand the agent.
        // A real upload, because a digest the control plane does not have is refused at the door.
        var upload = await (await _client.PostAsync("/api/packages", new ByteArrayContent(TestPackaging.ZipDirectory(RepoPaths.SampleServiceDir()))))
            .Content.ReadFromJsonAsync<UploadPackageResponse>();
        var broadRequest = new CreateApplicationPolicyRequest(
            ApplicationName: appName,
            Path: null,
            DesiredState: "Stopped",
            CronOverrides: null,
            TagSelector: new Dictionary<string, string> { ["env"] = "conflict-test" },
            PackageDigest: upload!.Digest);
        (await _client.PostAsJsonAsync("/api/application-policies", broadRequest)).EnsureSuccessStatusCode();

        (await _client.PutAsJsonAsync($"/api/agents/{agentName}/tags", new SetAgentTagsRequest(new Dictionary<string, string> { ["env"] = "conflict-test" })))
            .EnsureSuccessStatusCode();

        var resolved = await _client.GetFromJsonAsync<List<ApplicationPolicyDto>>($"/api/agents/{agentName}/policies");
        var entry = Assert.Single(resolved!);
        Assert.Equal(appName, entry.ApplicationName);
        Assert.NotNull(entry.ConflictReason);
        Assert.Contains(appName, entry.ConflictReason);

        // Both conflicting rules are still visible via the portal-facing endpoint — that's how an
        // operator actually finds and fixes the conflict.
        var portalView = await _client.GetFromJsonAsync<List<ApplicationPolicyDto>>($"/api/application-policies?agentName={agentName}");
        Assert.Equal(2, portalView!.Count);
    }

    [Fact]
    public async Task Creating_a_policy_rule_with_no_TagSelector_matches_every_agent()
    {
        var agentA = "any-a-" + Guid.NewGuid().ToString("N")[..8];
        var agentB = "any-b-" + Guid.NewGuid().ToString("N")[..8];
        var appName = RepoPaths.UniqueAppName();

        var request = new CreateApplicationPolicyRequest(
            ApplicationName: appName,
            Path: RepoPaths.SampleServiceDir(),
            DesiredState: "Running",
            CronOverrides: null,
            TagSelector: null);
        (await _client.PostAsJsonAsync("/api/application-policies", request)).EnsureSuccessStatusCode();

        var matchingA = await _client.GetFromJsonAsync<List<ApplicationPolicyDto>>($"/api/agents/{agentA}/policies");
        var matchingB = await _client.GetFromJsonAsync<List<ApplicationPolicyDto>>($"/api/agents/{agentB}/policies");

        Assert.Single(matchingA!, p => p.ApplicationName == appName);
        Assert.Single(matchingB!, p => p.ApplicationName == appName);
    }

    [Fact]
    public async Task Excluding_an_agent_from_scheduling_makes_it_qualify_for_nothing_even_a_rule_that_already_matches_it()
    {
        var agentName = "sched-" + Guid.NewGuid().ToString("N")[..8];
        var appName = RepoPaths.UniqueAppName();

        var request = new CreateApplicationPolicyRequest(
            ApplicationName: appName,
            Path: RepoPaths.SampleServiceDir(),
            DesiredState: "Running",
            CronOverrides: null,
            TagSelector: new Dictionary<string, string> { ["agent"] = agentName });
        (await _client.PostAsJsonAsync("/api/application-policies", request)).EnsureSuccessStatusCode();

        // Registers the agent and confirms the rule matches before exclusion — the baseline this test
        // is actually proving exclusion changes.
        var before = await _client.GetFromJsonAsync<List<ApplicationPolicyDto>>($"/api/agents/{agentName}/policies");
        Assert.Single(before!, p => p.ApplicationName == appName);

        var excludeResponse = await _client.PutAsJsonAsync($"/api/agents/{agentName}/scheduling", new SetAgentSchedulingRequest(false));
        excludeResponse.EnsureSuccessStatusCode();
        var excludedAgent = await excludeResponse.Content.ReadFromJsonAsync<AgentDto>();
        Assert.False(excludedAgent!.SchedulingEnabled);

        var whileExcluded = await _client.GetFromJsonAsync<List<ApplicationPolicyDto>>($"/api/agents/{agentName}/policies");
        Assert.Empty(whileExcluded!);

        // The portal-facing endpoint must agree — same shared resolution point, so it can't show a rule
        // "governing" an agent that the agent itself was just told to ignore entirely.
        var portalViewWhileExcluded = await _client.GetFromJsonAsync<List<ApplicationPolicyDto>>($"/api/application-policies?agentName={agentName}");
        Assert.Empty(portalViewWhileExcluded!);

        var includeResponse = await _client.PutAsJsonAsync($"/api/agents/{agentName}/scheduling", new SetAgentSchedulingRequest(true));
        includeResponse.EnsureSuccessStatusCode();

        var afterReinclude = await _client.GetFromJsonAsync<List<ApplicationPolicyDto>>($"/api/agents/{agentName}/policies");
        Assert.Single(afterReinclude!, p => p.ApplicationName == appName);
    }

    [Fact]
    public async Task Excluding_one_agent_does_not_affect_a_rule_matching_a_different_agent()
    {
        var excludedAgent = "excluded-" + Guid.NewGuid().ToString("N")[..8];
        var otherAgent = "other-" + Guid.NewGuid().ToString("N")[..8];
        var appName = RepoPaths.UniqueAppName();

        // A broad selector matching BOTH agents — the clearest proof exclusion is per-agent, not
        // somehow scoped to the rule itself.
        await _client.PutAsJsonAsync($"/api/agents/{excludedAgent}/tags", new SetAgentTagsRequest(new Dictionary<string, string> { ["role"] = "web" }));
        await _client.PutAsJsonAsync($"/api/agents/{otherAgent}/tags", new SetAgentTagsRequest(new Dictionary<string, string> { ["role"] = "web" }));

        var request = new CreateApplicationPolicyRequest(
            ApplicationName: appName,
            Path: RepoPaths.SampleServiceDir(),
            DesiredState: "Running",
            CronOverrides: null,
            TagSelector: new Dictionary<string, string> { ["role"] = "web" });
        (await _client.PostAsJsonAsync("/api/application-policies", request)).EnsureSuccessStatusCode();

        (await _client.PutAsJsonAsync($"/api/agents/{excludedAgent}/scheduling", new SetAgentSchedulingRequest(false))).EnsureSuccessStatusCode();

        var excludedView = await _client.GetFromJsonAsync<List<ApplicationPolicyDto>>($"/api/agents/{excludedAgent}/policies");
        var otherView = await _client.GetFromJsonAsync<List<ApplicationPolicyDto>>($"/api/agents/{otherAgent}/policies");

        Assert.Empty(excludedView!);
        Assert.Single(otherView!, p => p.ApplicationName == appName);
    }
}
