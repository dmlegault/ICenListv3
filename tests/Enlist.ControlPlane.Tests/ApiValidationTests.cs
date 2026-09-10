using System.Net;
using System.Net.Http.Json;

using Enlist.ControlPlane.Contracts;
using Enlist.TestSupport;

namespace Enlist.ControlPlane.Tests;

/// <summary>
/// What the API refuses at the door. Digests and application names become filesystem paths, process
/// names and container names on every agent, so a value that is merely "not found" here can be a path
/// there; a rule that names a package nobody uploaded fails only when an agent tries to download it;
/// and Kestrel's stock 30 MB body limit turned a real-sized upload into a bare 413.
/// </summary>
public sealed class ApiValidationTests : IAsyncLifetime
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
    public async Task A_digest_that_is_not_64_hex_characters_is_refused_before_it_can_become_a_path()
    {
        // "..\..\notes", URL-encoded — what a caller hoping to read outside the blob root would send.
        const string traversal = "..%5C..%5Cnotes";

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync($"/api/packages/{traversal}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync($"/api/packages/{traversal}/manifest")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.DeleteAsync($"/api/packages/{traversal}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/packages/abc")).StatusCode);

        // Well-formed but unknown is still simply not found.
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/packages/{new string('a', 64)}")).StatusCode);
    }

    [Fact]
    public async Task An_application_name_that_could_be_a_path_or_a_device_is_refused_on_both_endpoints()
    {
        var zip = TestPackaging.ZipDirectory(RepoPaths.SampleServiceDir());

        foreach (var bad in new[] { "..%5Cevil", "a%2Fb", "NUL", "con.exe", ".hidden", "trailing.", "has%20space" })
        {
            var upload = await _client.PostAsync($"/api/packages?application={bad}", new ByteArrayContent(zip));
            Assert.Equal(HttpStatusCode.BadRequest, upload.StatusCode);
        }

        foreach (var bad in new[] { "..\\evil", "a/b", "NUL", "con.exe", ".hidden", "trailing.", "has space", new string('x', 65) })
        {
            var create = await _client.PostAsJsonAsync("/api/application-policies", PathRule(bad));
            Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
            Assert.Contains("application name", await create.Content.ReadAsStringAsync());
        }

        var ok = await _client.PostAsJsonAsync("/api/application-policies", PathRule("billing-api.v2"));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    [Fact]
    public async Task A_rule_naming_a_digest_the_control_plane_does_not_have_is_refused_on_create_and_on_update()
    {
        var unknown = new string('f', 64);

        var create = await _client.PostAsJsonAsync("/api/application-policies", new CreateApplicationPolicyRequest(
            RepoPaths.UniqueAppName(), Path: null, "Running", null,
            TagSelector: new Dictionary<string, string> { ["agent"] = "some-agent" }, PackageDigest: unknown));
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Contains(unknown, await create.Content.ReadAsStringAsync());

        var created = await _client.PostAsJsonAsync("/api/application-policies", PathRule(RepoPaths.UniqueAppName()));
        var rule = (await created.Content.ReadFromJsonAsync<ApplicationPolicyDto>())!;

        var update = await _client.PutAsJsonAsync($"/api/application-policies/{rule.Id}", new UpdateApplicationPolicyRequest(null, null, null, PackageDigest: unknown));
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
        Assert.Contains(unknown, await update.Content.ReadAsStringAsync());

        // A digest that does exist — uploaded a moment ago — is accepted, and stored lowercase however it was written.
        var upload = await (await _client.PostAsync("/api/packages", new ByteArrayContent(TestPackaging.ZipDirectory(RepoPaths.SampleServiceDir())))).Content.ReadFromJsonAsync<UploadPackageResponse>();
        var accepted = await _client.PutAsJsonAsync($"/api/application-policies/{rule.Id}", new UpdateApplicationPolicyRequest(null, null, null, PackageDigest: upload!.Digest.ToUpperInvariant()));
        accepted.EnsureSuccessStatusCode();
        Assert.Equal(upload.Digest, (await accepted.Content.ReadFromJsonAsync<ApplicationPolicyDto>())!.PackageDigest);
    }

    [Fact]
    public async Task An_upload_larger_than_Kestrels_default_body_limit_is_accepted()
    {
        // Random text compresses poorly, so the zip stays comfortably over the 30,000,000-byte default.
        var rng = new Random(20260909);
        var chars = new char[60 * 1024 * 1024];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)('!' + rng.Next(90));
        }

        var filler = new string(chars);
        var zip = TestPackaging.ZipDirectory(RepoPaths.SampleServiceDir(), extraFileContent: filler);
        Assert.True(zip.Length > 30_000_000, $"test setup: the zip is only {zip.Length:N0} bytes, which would not exercise the limit.");

        var response = await _client.PostAsync($"/api/packages?application={RepoPaths.UniqueAppName()}", new ByteArrayContent(zip));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(zip.Length, (await response.Content.ReadFromJsonAsync<UploadPackageResponse>())!.SizeBytes);
    }

    [Fact]
    public async Task A_body_that_is_not_a_zip_is_refused_and_leaves_nothing_behind()
    {
        var blobsBefore = Directory.GetFiles(_server!.PackageStorageRoot, "*.zip").Length;

        var response = await _client.PostAsync("/api/packages?application=NotAZip", new ByteArrayContent("just some text, not an archive"u8.ToArray()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not a zip archive", await response.Content.ReadAsStringAsync());

        // Neither a row nor a blob: the bytes were saved to be hashed, then removed again.
        var packages = await _client.GetFromJsonAsync<List<PackageInfo>>("/api/packages");
        Assert.DoesNotContain(packages!, p => p.ApplicationName == "NotAZip");
        Assert.Equal(blobsBefore, Directory.GetFiles(_server.PackageStorageRoot, "*.zip").Length);
    }

    private static CreateApplicationPolicyRequest PathRule(string applicationName) =>
        new(applicationName, RepoPaths.SampleServiceDir(), "Running", null, TagSelector: new Dictionary<string, string> { ["agent"] = "some-agent" });
}
