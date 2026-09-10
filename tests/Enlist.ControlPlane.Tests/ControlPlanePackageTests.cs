using System.Net.Http.Json;
using System.Security.Cryptography;

using Enlist.ControlPlane.Contracts;
using Enlist.TestSupport;

namespace Enlist.ControlPlane.Tests;

/// <summary>API-level tests of package upload/download/dedup against the real control plane and real LocalDB — no agent involved, just proving the storage mechanism itself is correct.</summary>
public sealed class ControlPlanePackageTests : IAsyncLifetime
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
    public async Task Uploading_a_package_returns_its_actual_SHA256_digest_and_it_can_be_downloaded_back_byte_for_byte()
    {
        var bytes = TestPackaging.ZipDirectory(RepoPaths.SampleServiceDir());
        var expectedDigest = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var uploadResponse = await _client.PostAsync("/api/packages", new ByteArrayContent(bytes));
        uploadResponse.EnsureSuccessStatusCode();
        var uploaded = await uploadResponse.Content.ReadFromJsonAsync<UploadPackageResponse>();

        Assert.Equal(expectedDigest, uploaded!.Digest);
        Assert.Equal(bytes.Length, uploaded.SizeBytes);
        Assert.False(uploaded.AlreadyExisted);

        var downloadResponse = await _client.GetAsync($"/api/packages/{uploaded.Digest}");
        downloadResponse.EnsureSuccessStatusCode();
        var downloaded = await downloadResponse.Content.ReadAsByteArrayAsync();

        Assert.Equal(bytes, downloaded);
    }

    [Fact]
    public async Task Uploading_the_same_bytes_twice_is_idempotent_and_reports_AlreadyExisted()
    {
        var bytes = TestPackaging.ZipDirectory(RepoPaths.SampleServiceDir());

        var first = await (await _client.PostAsync("/api/packages", new ByteArrayContent(bytes))).Content.ReadFromJsonAsync<UploadPackageResponse>();
        var second = await (await _client.PostAsync("/api/packages", new ByteArrayContent(bytes))).Content.ReadFromJsonAsync<UploadPackageResponse>();

        Assert.False(first!.AlreadyExisted);
        Assert.True(second!.AlreadyExisted);
        Assert.Equal(first.Digest, second.Digest);

        var listed = await _client.GetFromJsonAsync<List<PackageInfo>>("/api/packages");
        Assert.Single(listed!, p => p.Digest == first.Digest);
    }

    [Fact]
    public async Task Downloading_an_unknown_digest_returns_404()
    {
        var response = await _client.GetAsync("/api/packages/0000000000000000000000000000000000000000000000000000000000000000");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }
    [Fact]
    public async Task Concurrent_uploads_for_one_application_get_distinct_version_numbers()
    {
        var appName = RepoPaths.UniqueAppName();

        // Eight different builds of one application, all in flight at once. Each computes "highest
        // existing + 1"; the unique index and the retry behind it are what keep two of them from both
        // becoming the same number.
        var uploads = Enumerable.Range(1, 8)
            .Select(i => _client.PostAsync($"/api/packages?application={appName}", new ByteArrayContent(TestPackaging.ZipDirectory(RepoPaths.SampleServiceDir(), extraFileContent: $"build {i}"))))
            .ToList();
        var responses = await Task.WhenAll(uploads);
        Assert.All(responses, r => r.EnsureSuccessStatusCode());

        var packages = (await _client.GetFromJsonAsync<List<PackageInfo>>("/api/packages"))!.Where(p => p.ApplicationName == appName).ToList();
        Assert.Equal(8, packages.Count);
        Assert.Equal(Enumerable.Range(1, 8), packages.Select(p => p.VersionNumber!.Value).OrderBy(v => v));
    }
}
