using System.Net.Http.Json;

using Enlist.Agent.Configuration;
using Enlist.Agent.Status;
using Enlist.ControlPlane.Contracts;
using Enlist.Runner.Protocol;
using Enlist.TestSupport;

namespace Enlist.Agent.Tests;

/// <summary>
/// The actual proof control-plane mode exists for: otherwise, applying a change means logging into each of N agents
/// individually and copying files by hand. These tests push a change through the REAL
/// Enlist.ControlPlane API (a separate spawned process, real LocalDB) and confirm a REAL AgentHost —
/// already running, never restarted — picks it up on its own and converges: starts a newly-assigned
/// application, stops one that's been turned off or removed, without anyone touching the agent.
/// </summary>
public sealed class ControlPlaneReconciliationTests : IAsyncLifetime
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "enlist-cp-recon-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _agentName = "test-agent-" + Guid.NewGuid().ToString("N")[..8];
    private ControlPlaneTestServer? _server;
    private AgentHost? _host;
    private HttpClient? _adminClient;

    public async Task InitializeAsync()
    {
        _server = await ControlPlaneTestServer.StartAsync(TimeSpan.FromSeconds(30));
        _adminClient = new HttpClient { BaseAddress = _server.BaseUri };
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
        }

        _adminClient?.Dispose();

        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch
        {
        }
    }

    private async Task<ApplicationPolicyDto> CreatePolicyAsync(string appName, string desiredState = "Running", Dictionary<string, string>? cronOverrides = null)
    {
        var request = new CreateApplicationPolicyRequest(
            appName, RepoPaths.SampleServiceDir(), desiredState, cronOverrides,
            TagSelector: new Dictionary<string, string> { ["agent"] = _agentName });
        var response = await _adminClient!.PostAsJsonAsync("/api/application-policies", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApplicationPolicyDto>())!;
    }

    private Task<HttpResponseMessage> UpdatePolicyAsync(Guid id, UpdateApplicationPolicyRequest request) =>
        _adminClient!.PutAsJsonAsync($"/api/application-policies/{id}", request);

    private async Task<AgentHost> StartAgentAsync()
    {
        var assignmentSource = new ControlPlaneAssignmentSource(_server!.BaseUri, _agentName, Path.Combine(_dataRoot, "Packages"));
        await assignmentSource.ConnectAsync(CancellationToken.None);

        var statusReporter = new ControlPlaneStatusReporter(new HttpClient { BaseAddress = _server.BaseUri }, _agentName);

        _host = new AgentHost(assignmentSource, RepoPaths.RunnerBinDirectory(), Path.Combine(_dataRoot, "Runners"), Path.Combine(_dataRoot, "Logs"), statusReporter: statusReporter);
        await _host.StartAsync();
        return _host;
    }

    [Fact]
    public async Task A_policy_rule_created_before_the_agent_starts_is_running_after_StartAsync()
    {
        var appName = RepoPaths.UniqueAppName();
        await CreatePolicyAsync(appName);

        var host = await StartAgentAsync();

        Assert.True(host.Instances.ContainsKey(appName));
    }

    [Fact]
    public async Task Creating_a_new_policy_rule_while_the_agent_is_already_running_starts_it_without_restarting_the_agent()
    {
        var host = await StartAgentAsync();
        Assert.Empty(host.Instances);

        var appName = RepoPaths.UniqueAppName();
        await CreatePolicyAsync(appName);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!host.Instances.ContainsKey(appName) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.True(host.Instances.ContainsKey(appName), "the agent never picked up the new policy rule via reconciliation.");
    }

    [Fact]
    public async Task Flipping_a_policy_rule_to_Stopped_stops_the_running_application_without_restarting_the_agent()
    {
        var appName = RepoPaths.UniqueAppName();
        var created = await CreatePolicyAsync(appName);

        var host = await StartAgentAsync();
        Assert.True(host.Instances.ContainsKey(appName));

        var response = await UpdatePolicyAsync(created.Id, new UpdateApplicationPolicyRequest(null, "Stopped", null));
        response.EnsureSuccessStatusCode();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (host.Instances.ContainsKey(appName) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.False(host.Instances.ContainsKey(appName), "the agent never stopped the application after its policy rule was flipped to Stopped.");
    }

    [Fact]
    public async Task Deleting_a_policy_rule_stops_the_running_application()
    {
        var appName = RepoPaths.UniqueAppName();
        var created = await CreatePolicyAsync(appName);

        var host = await StartAgentAsync();
        Assert.True(host.Instances.ContainsKey(appName));

        var response = await _adminClient!.DeleteAsync($"/api/application-policies/{created.Id}");
        response.EnsureSuccessStatusCode();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (host.Instances.ContainsKey(appName) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.False(host.Instances.ContainsKey(appName));
    }

    [Fact]
    public async Task Changing_a_cron_override_restarts_the_application_and_the_job_fires_on_the_new_schedule()
    {
        var appName = RepoPaths.UniqueAppName();
        // Declared cron is 2am — a cron override pointed at "far in the future" first, then updated
        // to fire every couple seconds, proving the CHANGE itself (not just the initial value) reaches
        // a running agent.
        var created = await CreatePolicyAsync(appName, cronOverrides: new Dictionary<string, string> { ["Sample Job"] = "0 0 0 1 1 *" });

        var host = await StartAgentAsync();
        Assert.True(host.Instances.ContainsKey(appName));

        var jobResult = new TaskCompletionSource<JobResultMessage>();
        host.MessageReceived += (_, message) =>
        {
            if (message is JobResultMessage { Job: "Sample Job" } jr)
            {
                jobResult.TrySetResult(jr);
            }
        };

        var response = await UpdatePolicyAsync(created.Id, new UpdateApplicationPolicyRequest(null, null, new Dictionary<string, string> { ["Sample Job"] = "*/2 * * * * *" }));
        response.EnsureSuccessStatusCode();

        var result = await jobResult.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(JobOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public async Task Status_reports_from_a_control_plane_backed_agent_land_in_the_control_plane()
    {
        var appName = RepoPaths.UniqueAppName();
        await CreatePolicyAsync(appName);

        await StartAgentAsync();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        HttpResponseMessage? latest = null;
        while (DateTime.UtcNow < deadline)
        {
            latest = await _adminClient!.GetAsync($"/api/agents/{_agentName}/report/latest");
            if (latest.IsSuccessStatusCode)
            {
                break;
            }

            await Task.Delay(200);
        }

        Assert.NotNull(latest);
        Assert.True(latest!.IsSuccessStatusCode, "no status report ever landed in the control plane for this agent.");

        var body = await latest.Content.ReadAsStringAsync();
        Assert.Contains(appName, body);
    }

    [Fact]
    public async Task A_policy_rule_targeting_a_tag_selector_reaches_a_running_agent_whose_tags_match()
    {
        await _adminClient!.PutAsJsonAsync($"/api/agents/{_agentName}/tags", new SetAgentTagsRequest(new Dictionary<string, string> { ["role"] = "web" }));

        var appName = RepoPaths.UniqueAppName();
        var request = new CreateApplicationPolicyRequest(
            ApplicationName: appName,
            Path: RepoPaths.SampleServiceDir(),
            DesiredState: "Running",
            CronOverrides: null,
            TagSelector: new Dictionary<string, string> { ["role"] = "web" });
        (await _adminClient!.PostAsJsonAsync("/api/application-policies", request)).EnsureSuccessStatusCode();

        // Setting the agent's tags BEFORE the agent's first fetch means this isn't testing "did a push
        // arrive," it's testing that GetCurrentAsync's own selector resolution works at all — the
        // live-push path is already covered by the self-tag tests above.
        var host = await StartAgentAsync();

        Assert.True(host.Instances.ContainsKey(appName));
    }
}
