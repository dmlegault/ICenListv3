using System.Net.Http.Json;

using Enlist.Agent.Configuration;
using Enlist.Agent.Status;
using Enlist.ControlPlane.Contracts;
using Enlist.Runner.Protocol;
using Enlist.TestSupport;

namespace Enlist.Agent.Tests;

/// <summary>
/// The other half of what today means "copy files to each agent manually": a policy rule can name a
/// content-addressed package instead of a pre-existing local path, and the agent downloads, verifies,
/// and extracts it itself. These drive the real control plane and a real AgentHost the same way
/// ControlPlaneReconciliationTests does, just with PackageDigest instead of Path.
/// </summary>
public sealed class PackageDistributionTests : IAsyncLifetime
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "enlist-pkg-dist-tests-" + Guid.NewGuid().ToString("N"));
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

    private async Task<string> UploadPackageAsync(byte[] bytes)
    {
        var response = await _adminClient!.PostAsync("/api/packages", new ByteArrayContent(bytes));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UploadPackageResponse>())!.Digest;
    }

    private async Task<ApplicationPolicyDto> CreatePackagePolicyAsync(string appName, string digest)
    {
        var request = new CreateApplicationPolicyRequest(
            appName, Path: null, "Running", CronOverrides: null,
            TagSelector: new Dictionary<string, string> { ["agent"] = _agentName }, PackageDigest: digest);
        var response = await _adminClient!.PostAsJsonAsync("/api/application-policies", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApplicationPolicyDto>())!;
    }

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
    public async Task A_policy_rule_backed_by_a_package_digest_is_downloaded_extracted_and_run_correctly()
    {
        var digest = await UploadPackageAsync(TestPackaging.ZipDirectory(RepoPaths.SampleServiceDir()));
        var appName = RepoPaths.UniqueAppName();
        await CreatePackagePolicyAsync(appName, digest);

        var heartbeatSeen = new TaskCompletionSource();
        var hostTask = StartAgentAsync();
        var host = await hostTask;
        host.MessageReceived += (_, message) =>
        {
            if (message is LogMessage { Source: "Sample Service" } log && log.Text.StartsWith("heartbeat"))
            {
                heartbeatSeen.TrySetResult();
            }
        };

        Assert.True(host.Instances.ContainsKey(appName));

        // Proves it actually came from the downloaded+extracted package, not some coincidental local
        // path — the extraction target is the content-addressed cache, keyed by digest.
        var extractedDir = Path.Combine(_dataRoot, "Packages", digest);
        Assert.True(Directory.Exists(extractedDir));
        Assert.True(File.Exists(Path.Combine(extractedDir, "Enlist.Sample.Service.dll")));

        await heartbeatSeen.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Two_applications_assigned_the_same_digest_both_run_from_the_one_shared_cached_extraction()
    {
        var digest = await UploadPackageAsync(TestPackaging.ZipDirectory(RepoPaths.SampleServiceDir()));
        var appA = RepoPaths.UniqueAppName();
        var appB = RepoPaths.UniqueAppName();
        await CreatePackagePolicyAsync(appA, digest);
        await CreatePackagePolicyAsync(appB, digest);

        var host = await StartAgentAsync();

        Assert.True(host.Instances.ContainsKey(appA));
        Assert.True(host.Instances.ContainsKey(appB));

        // Exactly one extraction directory for the shared digest, not one per application.
        var packagesRoot = Path.Combine(_dataRoot, "Packages");
        Assert.Single(Directory.GetDirectories(packagesRoot), d => Path.GetFileName(d) == digest);
    }

    [Fact]
    public async Task Updating_a_policy_rule_to_a_new_digest_redeploys_the_application_onto_the_new_package()
    {
        var digestV1 = await UploadPackageAsync(TestPackaging.ZipDirectory(RepoPaths.SampleServiceDir(), extraFileContent: "v1"));
        var digestV2 = await UploadPackageAsync(TestPackaging.ZipDirectory(RepoPaths.SampleServiceDir(), extraFileContent: "v2"));
        Assert.NotEqual(digestV1, digestV2);

        var appName = RepoPaths.UniqueAppName();
        var created = await CreatePackagePolicyAsync(appName, digestV1);

        var host = await StartAgentAsync();
        Assert.True(host.Instances.ContainsKey(appName));
        var originalPid = host.Instances[appName].Pid;

        var updateResponse = await _adminClient!.PutAsJsonAsync(
            $"/api/application-policies/{created.Id}",
            new UpdateApplicationPolicyRequest(Path: null, DesiredState: null, CronOverrides: null, PackageDigest: digestV2));
        updateResponse.EnsureSuccessStatusCode();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (host.Instances.TryGetValue(appName, out var instance) && instance.Pid != originalPid)
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert.True(host.Instances.TryGetValue(appName, out var finalInstance));
        Assert.NotEqual(originalPid, finalInstance!.Pid);

        var extractedV2 = Path.Combine(_dataRoot, "Packages", digestV2, "version-marker.txt");
        Assert.True(File.Exists(extractedV2));
        Assert.Equal("v2", await File.ReadAllTextAsync(extractedV2));

        // The agent's own local cache prunes once a digest is no longer in use anywhere on this agent
        // (unlike the control plane's blob store, which keeps a grace period for rollback) — but
        // pruning runs AFTER reconciliation, which is itself only partway done at the moment the loop
        // above first observes the new Pid (that happens early inside StartApplicationAsync, well
        // before ReconcileAsync — and therefore pruning — has finished). So this polls for the actual
        // outcome instead of assuming it's already landed the instant the Pid changed.
        var extractedV1Dir = Path.Combine(_dataRoot, "Packages", digestV1);
        var pruneDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Directory.Exists(extractedV1Dir) && DateTime.UtcNow < pruneDeadline)
        {
            await Task.Delay(100);
        }

        Assert.False(Directory.Exists(extractedV1Dir), "the superseded digest's local cache should have been pruned after the redeploy.");
    }
}
