using System.Net.Http.Json;
using System.Text.Json;

using Enlist.Agent.Configuration;
using Enlist.Agent.Status;
using Enlist.ControlPlane.Contracts;
using Enlist.Runner.Protocol;
using Enlist.TestSupport;

namespace Enlist.Agent.Tests;

/// <summary>
/// AgentHost’s runner-bin SELECTION by RuntimeFlavor (see docs/03-architecture/SAD.md §9): that AgentHost
/// actually uses the path registered for a given flavor rather than the default. It proves that by
/// registering the one real (net10.0) runner build under a second flavor name and confirming it is
/// the one used, which keeps the test about selection and nothing else.
///
/// It does not claim to prove a real net472 plugin can be hosted. That is
/// LegacyApplicationEndToEndTests, added 2026-09-13 once both halves existed — and note what it took
/// to make THAT test mean anything: written the obvious way it passed with the net10.0 runner
/// registered under the net472 flavor, because .NET 10 loads the sample’s simple net472 assembly
/// perfectly well. It has to check which runner build is actually executing.
/// </summary>
public sealed class RuntimeFlavorRoutingTests : IAsyncLifetime
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "enlist-agent-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _appName = RepoPaths.UniqueAppName();
    private AgentHost? _host;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
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
    public async Task An_assignment_declaring_an_unconfigured_RuntimeFlavor_is_marked_Failed_and_never_started()
    {
        var app = new ApplicationAssignment { Name = _appName, Path = RepoPaths.SampleServiceDir(), RuntimeFlavor = RuntimeFlavors.NetFramework472 };
        var assignments = new AgentAssignments { Applications = { app } };

        // No additionalRunnerBinDirectories supplied — this agent only knows RuntimeFlavors.Default.
        _host = new AgentHost(assignments, RepoPaths.RunnerBinDirectory(), Path.Combine(_dataRoot, "Runners"), Path.Combine(_dataRoot, "Logs"));

        await _host.StartAsync();

        Assert.False(_host.Instances.ContainsKey(_appName));

        var json = await File.ReadAllTextAsync(_host.StatusFilePath);
        var snapshot = JsonSerializer.Deserialize<AgentStatusSnapshot>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var appStatus = Assert.Single(snapshot!.Applications);
        Assert.Equal(ApplicationState.Failed, appStatus.State);
        Assert.Null(appStatus.Pid);
    }

    [Fact]
    public async Task Starts_an_application_from_its_configured_secondary_runner_bin()
    {
        var app = new ApplicationAssignment { Name = _appName, Path = RepoPaths.SampleServiceDir(), RuntimeFlavor = RuntimeFlavors.NetFramework472 };
        var assignments = new AgentAssignments { Applications = { app } };

        var additionalRunnerBinDirectories = new Dictionary<string, string> { [RuntimeFlavors.NetFramework472] = RepoPaths.RunnerBinDirectory() };
        _host = new AgentHost(
            assignments,
            runnerBinDirectory: "C:\\this-path-does-not-exist-and-must-never-be-used-for-a-net472-assignment",
            Path.Combine(_dataRoot, "Runners"),
            Path.Combine(_dataRoot, "Logs"),
            additionalRunnerBinDirectories: additionalRunnerBinDirectories);

        var heartbeatSeen = new TaskCompletionSource();
        _host.MessageReceived += (_, message) =>
        {
            if (message is LogMessage { Source: "Sample Service" } log && log.Text.StartsWith("heartbeat"))
            {
                heartbeatSeen.TrySetResult();
            }
        };

        await _host.StartAsync();

        Assert.True(_host.Instances.ContainsKey(_appName));
        await heartbeatSeen.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Changing_only_RuntimeFlavor_on_an_already_running_policy_rule_restarts_it_via_live_reconciliation()
    {
        // Mirrors ControlPlaneReconciliationTests' own pattern (real spawned control plane, a running
        // AgentHost that's never restarted, a real PUT) — the one thing this test adds is that only
        // RuntimeFlavor changes between the two PUTs, proving ReconcileAsync's changed-detection
        // actually compares it (see AgentHost.ReconcileAsync), not just Path/CronOverrides.
        await using var server = await ControlPlaneTestServer.StartAsync(TimeSpan.FromSeconds(30));
        using var adminClient = new HttpClient { BaseAddress = server.BaseUri };
        var agentName = "test-agent-" + Guid.NewGuid().ToString("N")[..8];

        var create = new CreateApplicationPolicyRequest(
            _appName, RepoPaths.SampleServiceDir(), "Running", CronOverrides: null,
            TagSelector: new Dictionary<string, string> { ["agent"] = agentName }, RuntimeFlavor: RuntimeFlavors.Default);
        var created = await adminClient.PostAsJsonAsync("/api/application-policies", create);
        created.EnsureSuccessStatusCode();
        var createdDto = (await created.Content.ReadFromJsonAsync<ApplicationPolicyDto>())!;

        var additionalRunnerBinDirectories = new Dictionary<string, string> { [RuntimeFlavors.NetFramework472] = RepoPaths.RunnerBinDirectory() };
        var assignmentSource = new ControlPlaneAssignmentSource(server.BaseUri, agentName, Path.Combine(_dataRoot, "Packages"));
        await assignmentSource.ConnectAsync(CancellationToken.None);
        _host = new AgentHost(assignmentSource, RepoPaths.RunnerBinDirectory(), Path.Combine(_dataRoot, "Runners"), Path.Combine(_dataRoot, "Logs"), additionalRunnerBinDirectories: additionalRunnerBinDirectories);
        await _host.StartAsync();

        Assert.True(_host.Instances.ContainsKey(_appName));
        var firstPid = _host.Instances[_appName].Pid;

        var update = new UpdateApplicationPolicyRequest(Path: null, DesiredState: null, CronOverrides: null, PackageDigest: null, RuntimeFlavor: RuntimeFlavors.NetFramework472);
        (await adminClient.PutAsJsonAsync($"/api/application-policies/{createdDto.Id}", update)).EnsureSuccessStatusCode();

        // Not "still present with the old pid" — that check would exit the moment
        // StopApplicationAsync momentarily removes the instance, before StartApplicationAsync re-adds
        // it under its new pid, reporting "stopped and never came back" on what's actually just an
        // in-flight restart.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && !(_host.Instances.TryGetValue(_appName, out var current) && current.Pid != firstPid))
        {
            await Task.Delay(100);
        }

        Assert.True(_host.Instances.ContainsKey(_appName), "the application was stopped and never came back up under the new RuntimeFlavor.");
        Assert.NotEqual(firstPid, _host.Instances[_appName].Pid);
    }
}
