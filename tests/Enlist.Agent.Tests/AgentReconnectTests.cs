using System.Net.Http.Json;

using Enlist.Agent.Configuration;
using Enlist.Agent.Status;
using Enlist.ControlPlane.Contracts;
using Enlist.TestSupport;

namespace Enlist.Agent.Tests;

/// <summary>
/// What an agent does when the control plane goes away and comes back — against a REAL control plane
/// killed and relaunched on the same address, which is what a deploy or a reboot looks like from the
/// agent's side. Before this was handled, SignalR's stock reconnect policy gave up after four attempts
/// (about 42 seconds) and nothing handled Closed: an agent that outlived a longer outage kept
/// heartbeating over HTTP, showed Online in the portal, and never received another push for the rest
/// of its life.
/// </summary>
public sealed class AgentReconnectTests : IAsyncLifetime
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "enlist-reconnect-tests-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public async Task An_agent_keeps_receiving_pushes_after_an_outage_longer_than_the_stock_reconnect_window()
    {
        // The re-fetch floor is parked an hour away, so the only route by which a change can reach the
        // agent in this test is the push channel — which is the thing under test.
        var host = await StartAgentAsync(refetchInterval: TimeSpan.FromHours(1));

        var appName = RepoPaths.UniqueAppName();
        var rule = await CreatePolicyAsync(appName);
        await WaitUntilAsync(() => host.Instances.ContainsKey(appName), TimeSpan.FromSeconds(30),
            "the agent never started the application from the first push - the push path is broken before the outage even begins.");

        await _server!.StopAsync();

        // The agent notices on its own, and reports it as the state the portal shows.
        await WaitUntilAsync(async () => (await host.GetCapabilitiesAsync()).PushChannel == PushChannelStates.Reconnecting, TimeSpan.FromSeconds(30),
            "the agent never reported its push channel as reconnecting after the control plane went away.");

        // Longer than the 0 + 2 + 10 + 30 seconds the stock policy tolerates before giving up for good.
        await Task.Delay(TimeSpan.FromSeconds(50));

        await _server.StartAgainAsync(TimeSpan.FromSeconds(30));

        // A change made after the restart has to arrive over the push channel (or the catch-up fetch a
        // successful reconnect performs) — the floor is an hour away.
        var response = await _adminClient!.PutAsJsonAsync($"/api/application-policies/{rule.Id}", new UpdateApplicationPolicyRequest(null, "Stopped", null));
        response.EnsureSuccessStatusCode();
        await WaitUntilAsync(() => !host.Instances.ContainsKey(appName), TimeSpan.FromSeconds(60),
            "the agent never stopped the application: a policy change made after the control plane came back did not reach it.");

        // And the control plane has been told the channel is back, so the portal's chip clears.
        await WaitUntilAsync(async () => (await GetAgentAsync()).Capabilities?.PushChannel == PushChannelStates.Connected, TimeSpan.FromSeconds(30),
            "the control plane was never told the agent's push channel was connected again.");

        var agentLog = string.Join(Environment.NewLine, Directory.GetFiles(Path.Combine(_dataRoot, "Logs"), "agent-*.log").Select(File.ReadAllText));
        Assert.Contains("push connection lost", agentLog);
        Assert.Contains("push connection restored", agentLog);
    }

    [Fact]
    public async Task An_agent_whose_push_channel_never_came_up_still_converges_on_the_refetch_floor()
    {
        // ConnectAsync is never called: there is no push channel at all, so only the periodic re-fetch
        // can deliver the change below.
        var host = await StartAgentAsync(refetchInterval: TimeSpan.FromSeconds(2), connectHub: false);
        Assert.Empty(host.Instances);

        var appName = RepoPaths.UniqueAppName();
        await CreatePolicyAsync(appName);

        await WaitUntilAsync(() => host.Instances.ContainsKey(appName), TimeSpan.FromSeconds(30),
            "the agent never started the application: with no push channel, only the re-fetch floor can deliver a change, and it did not.");

        await WaitUntilAsync(async () => (await GetAgentAsync()).Capabilities?.PushChannel == PushChannelStates.Disconnected, TimeSpan.FromSeconds(30),
            "the control plane was never told this agent's push channel is down, so the portal would show it as receiving.");
    }

    private async Task<AgentHost> StartAgentAsync(TimeSpan refetchInterval, bool connectHub = true)
    {
        var assignmentSource = new ControlPlaneAssignmentSource(_server!.BaseUri, _agentName, Path.Combine(_dataRoot, "Packages"), refetchInterval);
        if (connectHub)
        {
            await assignmentSource.ConnectAsync(CancellationToken.None);
        }

        var statusReporter = new ControlPlaneStatusReporter(new HttpClient { BaseAddress = _server.BaseUri }, _agentName);

        _host = new AgentHost(assignmentSource, RepoPaths.RunnerBinDirectory(), Path.Combine(_dataRoot, "Runners"), Path.Combine(_dataRoot, "Logs"), statusReporter: statusReporter);
        await _host.StartAsync();
        return _host;
    }

    private async Task<ApplicationPolicyDto> CreatePolicyAsync(string appName)
    {
        var request = new CreateApplicationPolicyRequest(
            appName, RepoPaths.SampleServiceDir(), "Running", null,
            TagSelector: new Dictionary<string, string> { ["agent"] = _agentName });
        var response = await _adminClient!.PostAsJsonAsync("/api/application-policies", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApplicationPolicyDto>())!;
    }

    private async Task<AgentDto> GetAgentAsync()
    {
        var agents = await _adminClient!.GetFromJsonAsync<List<AgentDto>>("/api/agents");
        return agents!.Single(a => a.Name == _agentName);
    }

    private static Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string failure) =>
        WaitUntilAsync(() => Task.FromResult(condition()), timeout, failure);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string failure)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The control plane is allowed to be mid-restart while a condition is polled.
            }

            await Task.Delay(250);
        }

        Assert.Fail(failure);
    }
}
