using System.Net;
using System.Net.Http.Json;

using Enlist.ControlPlane.Contracts;
using Enlist.TestSupport;

namespace Enlist.ControlPlane.Tests;

/// <summary>
/// Two related rules about how a policy's runtime flavor is decided, and what it may be combined with.
///
/// Both were found by asking a simple question of the running system: "what happens if I deploy a
/// net472 application and assign it to a container?" The answer at the time was "it runs, apparently
/// fine" — for two compounding reasons. The policy's flavor defaulted to net10.0 regardless of what the
/// package actually was, so upload-time detection never reached the thing that routes; and nothing
/// rejected net472-in-a-container, whose real failure is silent (assemblies that cannot load are
/// SKIPPED by discovery, so the application reports Running with zero services).
/// </summary>
public sealed class FlavorIsolationTests : IAsyncLifetime
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

    /// <summary>Uploads a real net472 build — LegacySample has no .deps.json, which is exactly what the server's detection keys on, so this exercises the genuine article rather than a fixture pretending.</summary>
    private async Task<string> UploadLegacyPackageAsync(string applicationName)
    {
        var zipPath = Path.Combine(Path.GetTempPath(), "enlist-flavor-" + Guid.NewGuid().ToString("N") + ".zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(RepoPaths.LegacySampleDir(), zipPath);

        try
        {
            // The body IS the zip — no multipart. See the /api/packages handler, which streams
            // request.Body straight into the content-addressed blob store.
            var response = await _client.PostAsync(
                $"/api/packages?application={applicationName}",
                new ByteArrayContent(await File.ReadAllBytesAsync(zipPath)));
            response.EnsureSuccessStatusCode();

            var uploaded = await response.Content.ReadFromJsonAsync<UploadPackageResponse>();
            Assert.Equal(RuntimeFlavors.NetFramework472, uploaded!.RuntimeFlavor);
            return uploaded.Digest;
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task A_policy_pointing_at_a_package_inherits_that_packages_detected_flavor()
    {
        var application = RepoPaths.UniqueAppName();
        var digest = await UploadLegacyPackageAsync(application);

        var response = await _client.PostAsJsonAsync("/api/application-policies", new CreateApplicationPolicyRequest(
            ApplicationName: application,
            Path: null,
            DesiredState: "Running",
            CronOverrides: null,
            TagSelector: SelfTag("flavor-agent"),
            PackageDigest: digest,
            RuntimeFlavor: null));

        response.EnsureSuccessStatusCode();
        var policy = await response.Content.ReadFromJsonAsync<ApplicationPolicyDto>();

        // The whole point: without derivation the rule would say net10.0 — the default — while the
        // package it points at is net472, and the AGENT routes on the rule. Upload-time detection was
        // therefore correct and completely inert.
        Assert.Equal(RuntimeFlavors.NetFramework472, policy!.RuntimeFlavor);
    }

    [Fact]
    public async Task Claiming_a_flavor_the_package_contradicts_is_rejected()
    {
        var application = RepoPaths.UniqueAppName();
        var digest = await UploadLegacyPackageAsync(application);

        var response = await _client.PostAsJsonAsync("/api/application-policies", new CreateApplicationPolicyRequest(
            ApplicationName: application,
            Path: null,
            DesiredState: "Running",
            CronOverrides: null,
            TagSelector: SelfTag("flavor-agent"),
            PackageDigest: digest,
            RuntimeFlavor: RuntimeFlavors.Default));

        // Rejected rather than silently resolved either way: the package's flavor came from inspecting
        // the actual bytes, the caller's is a claim about them, and quietly picking one would leave the
        // other wrong with nothing to show for it.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(RuntimeFlavors.NetFramework472, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_net472_package_cannot_be_assigned_to_a_container()
    {
        var application = RepoPaths.UniqueAppName();
        var digest = await UploadLegacyPackageAsync(application);

        var response = await _client.PostAsJsonAsync("/api/application-policies", new CreateApplicationPolicyRequest(
            ApplicationName: application,
            Path: null,
            DesiredState: "Running",
            CronOverrides: null,
            TagSelector: SelfTag("flavor-agent"),
            PackageDigest: digest,
            RuntimeFlavor: null,
            Isolation: new IsolationSpec(IsolationModes.Container)));

        // The runner image is Linux .NET 10, so there is no .NET Framework in it. Caught here because
        // the runtime symptom is invisible: discovery SKIPS assemblies it cannot load, so the
        // application would report Running with nothing in it and no error anywhere.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("cannot run in a container", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Switching_an_existing_container_rule_onto_a_net472_package_is_also_rejected()
    {
        var application = RepoPaths.UniqueAppName();

        // Starts life legitimately: a Path-based container rule, nothing to contradict.
        var created = await _client.PostAsJsonAsync("/api/application-policies", new CreateApplicationPolicyRequest(
            ApplicationName: application,
            Path: RepoPaths.SampleServiceDir(),
            DesiredState: "Running",
            CronOverrides: null,
            TagSelector: SelfTag("flavor-agent"),
            PackageDigest: null,
            RuntimeFlavor: null,
            Isolation: new IsolationSpec(IsolationModes.Container)));

        created.EnsureSuccessStatusCode();
        var policy = await created.Content.ReadFromJsonAsync<ApplicationPolicyDto>();

        var digest = await UploadLegacyPackageAsync(application);

        // The update mentions only the package — the container setting is untouched. Validating the
        // RESULTING combination rather than just the changed field is what catches this.
        var updated = await _client.PutAsJsonAsync($"/api/application-policies/{policy!.Id}",
            new UpdateApplicationPolicyRequest(Path: null, DesiredState: null, CronOverrides: null, PackageDigest: digest));

        Assert.Equal(HttpStatusCode.BadRequest, updated.StatusCode);
        Assert.Contains("cannot run in a container", await updated.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_path_based_rule_keeps_its_flavor_across_an_unrelated_update()
    {
        var application = RepoPaths.UniqueAppName();

        var created = await _client.PostAsJsonAsync("/api/application-policies", new CreateApplicationPolicyRequest(
            ApplicationName: application,
            Path: RepoPaths.LegacySampleDir(),
            DesiredState: "Running",
            CronOverrides: null,
            TagSelector: SelfTag("flavor-agent"),
            PackageDigest: null,
            RuntimeFlavor: RuntimeFlavors.NetFramework472));

        created.EnsureSuccessStatusCode();
        var policy = await created.Content.ReadFromJsonAsync<ApplicationPolicyDto>();

        var updated = await _client.PutAsJsonAsync($"/api/application-policies/{policy!.Id}",
            new UpdateApplicationPolicyRequest(Path: null, DesiredState: "Stopped", CronOverrides: null));

        updated.EnsureSuccessStatusCode();
        var result = await updated.Content.ReadFromJsonAsync<ApplicationPolicyDto>();

        // A Path-based rule has no package to inspect, so deriving unconditionally on update would have
        // silently reset this to net10.0 — which is exactly the bug the derivation was added to fix,
        // reintroduced from the other direction.
        Assert.Equal(RuntimeFlavors.NetFramework472, result!.RuntimeFlavor);
    }
}
