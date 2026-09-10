using System.Net;
using System.Net.Http.Json;

using Enlist.ControlPlane.Contracts;
using Enlist.TestSupport;

namespace Enlist.ControlPlane.Tests;

/// <summary>
/// Mark-and-sweep package retention, run on a testable timescale (real seconds instead of the real
/// 30-day/6-hour defaults) via env var overrides — see ControlPlaneTestServer.StartAsync. The point
/// under test isn't just "unreferenced things eventually get deleted," it's that a package currently
/// referenced by a policy rule is NEVER touched regardless of age, and that becoming referenced again
/// (a rollback) clears any in-progress grace period — the two properties that make this safe to run
/// automatically rather than something that could silently break a rollback.
/// </summary>
public sealed class PackageRetentionTests : IAsyncLifetime
{
    // Deliberately short but not razor-thin — real process/DB/HTTP jitter needs margin, and the sweep
    // loop's own Task.Delay granularity means "just barely longer than retention" is flaky in practice.
    private static readonly Dictionary<string, string> FastRetentionEnv = new()
    {
        ["PackageRetention__SweepInterval"] = "00:00:00.300",
        ["PackageRetention__RetentionPeriod"] = "00:00:01",
    };

    private ControlPlaneTestServer? _server;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _server = await ControlPlaneTestServer.StartAsync(TimeSpan.FromSeconds(30), FastRetentionEnv);
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

    private async Task<string> UploadAsync(string? extraFileContent = null)
    {
        var bytes = TestPackaging.ZipDirectory(RepoPaths.SampleServiceDir(), extraFileContent);
        var response = await _client.PostAsync("/api/packages", new ByteArrayContent(bytes));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UploadPackageResponse>())!.Digest;
    }

    private async Task<bool> ExistsAsync(string digest) =>
        (await _client.GetAsync($"/api/packages/{digest}")).StatusCode != HttpStatusCode.NotFound;

    [Fact]
    public async Task An_unreferenced_package_is_deleted_once_the_retention_period_elapses()
    {
        var digest = await UploadAsync();
        Assert.True(await ExistsAsync(digest));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (await ExistsAsync(digest) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
        }

        Assert.False(await ExistsAsync(digest), "an unreferenced package should have been swept away well within 10s at this retention/interval.");
    }

    [Fact]
    public async Task A_package_referenced_by_a_policy_rule_is_never_deleted_no_matter_how_long_it_waits()
    {
        var digest = await UploadAsync();
        var request = new CreateApplicationPolicyRequest(
            ApplicationName: RepoPaths.UniqueAppName(), Path: null, DesiredState: "Running", CronOverrides: null,
            TagSelector: new Dictionary<string, string> { ["agent"] = "some-agent" }, PackageDigest: digest);
        (await _client.PostAsJsonAsync("/api/application-policies", request)).EnsureSuccessStatusCode();

        // Comfortably longer than several full retention+sweep cycles.
        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.True(await ExistsAsync(digest), "a package still referenced by a live policy rule must never be deleted, regardless of age.");
    }

    [Fact]
    public async Task A_package_that_becomes_unreferenced_and_is_then_referenced_again_is_not_deleted()
    {
        var digestA = await UploadAsync(extraFileContent: "a");
        var digestB = await UploadAsync(extraFileContent: "b");
        Assert.NotEqual(digestA, digestB);

        var created = await _client.PostAsJsonAsync("/api/application-policies", new CreateApplicationPolicyRequest(
            ApplicationName: RepoPaths.UniqueAppName(), Path: null, DesiredState: "Running", CronOverrides: null,
            TagSelector: new Dictionary<string, string> { ["agent"] = "some-agent" }, PackageDigest: digestA));
        var policy = (await created.Content.ReadFromJsonAsync<ApplicationPolicyDto>())!;

        // Point the policy rule at B — A becomes unreferenced. Wait long enough for at least one sweep
        // to mark it (interval 300ms), but well under the 1s retention, so it's marked, not deleted.
        (await _client.PutAsJsonAsync($"/api/application-policies/{policy.Id}", new UpdateApplicationPolicyRequest(Path: null, DesiredState: null, CronOverrides: null, PackageDigest: digestB)))
            .EnsureSuccessStatusCode();
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.True(await ExistsAsync(digestA), "sanity check: A should still be alive at this point, only just marked unreferenced.");

        // Roll back to A — clears the mark. Wait past what would have been A's original deletion time.
        (await _client.PutAsJsonAsync($"/api/application-policies/{policy.Id}", new UpdateApplicationPolicyRequest(Path: null, DesiredState: null, CronOverrides: null, PackageDigest: digestA)))
            .EnsureSuccessStatusCode();
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.True(await ExistsAsync(digestA), "rolling back to A before its grace period expired should have cleared the mark entirely, not just paused the clock.");
    }
}
