using System.Net;
using System.Net.Http.Json;

using Enlist.ControlPlane.Contracts;
using Enlist.TestSupport;

namespace Enlist.ControlPlane.Tests;

/// <summary>
/// Deleting a package while somebody is downloading it.
///
/// This is one line of PackageBlobStore - the FileShare flags OpenRead asks for - and it has no
/// visible symptom until two things happen at once, which is why it went unnoticed. File.OpenRead
/// grants FileShare.Read and nothing else, so on Windows the file cannot be deleted while the read is
/// open: File.Delete throws a sharing violation. The retention sweep swallowed that, logged "could
/// not delete the blob", and tried again six hours later; the DELETE endpoint swallowed it too, and
/// removed the row anyway, orphaning the file with nothing left to retry it.
///
/// Deliberately NOT run against the fast-retention server PackageRetentionTests uses. With a
/// one-second retention the sweeper would be racing this test for the same package, and a test that
/// can lose its subject to a background service before it acts proves nothing. The default 30-day
/// retention means only this test ever touches it.
/// </summary>
public sealed class PackageBlobSharingTests : IAsyncLifetime
{
    private ControlPlaneTestServer? _server;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _server = await ControlPlaneTestServer.StartAsync(TimeSpan.FromSeconds(30), extraEnvironment: null);
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
    public async Task A_package_can_be_deleted_while_it_is_being_downloaded_and_the_download_still_completes()
    {
        var bytes = TestPackaging.ZipDirectory(RepoPaths.SampleServiceDir(), IncompressibleFiller());
        var upload = await _client.PostAsync("/api/packages", new ByteArrayContent(bytes));
        upload.EnsureSuccessStatusCode();
        var digest = (await upload.Content.ReadFromJsonAsync<UploadPackageResponse>())!.Digest;

        // ResponseHeadersRead, and the response is NOT disposed until the end: the server has the blob
        // open and is waiting to stream it. This is the state that used to make the delete impossible.
        using var inFlight = await _client.GetAsync($"/api/packages/{digest}", HttpCompletionOption.ResponseHeadersRead);
        inFlight.EnsureSuccessStatusCode();
        await using var body = await inFlight.Content.ReadAsStreamAsync();
        var firstByte = body.ReadByte();
        Assert.NotEqual(-1, firstByte);

        var deleted = await _client.DeleteAsync($"/api/packages/{digest}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // The blob itself, not just the row: this endpoint consults the blob store alone, so a 404
        // here means the file is genuinely gone rather than merely unreferenced.
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/packages/{digest}")).StatusCode);

        // And the reader that was already going gets everything it asked for. Windows unlinks the name
        // and frees the bytes when the last handle closes, so a delete never truncates a read in
        // progress - the half-downloaded package this would otherwise risk is the reason to check.
        using var rest = new MemoryStream();
        await body.CopyToAsync(rest);
        Assert.Equal(bytes.Length, rest.Length + 1);
    }

    /// <summary>
    /// Why the package under test is deliberately BIG (about 2 MB on disk, from 2.7 MB of
    /// base64-of-random that a zip cannot squeeze).
    ///
    /// The sample service packs to roughly 15 KB, which fits entirely inside Kestrel's response pipe
    /// and the socket send buffer. The server therefore finishes writing it, and CLOSES the file,
    /// before a client that only read the headers has asked for a single byte - so there is no read in
    /// flight to collide with, and this test passed against the broken code. A package far larger than
    /// those buffers cannot be handed off in one go: the server is still holding the file open,
    /// waiting for the client to drain it, which is precisely the state under test. Do not shrink it.
    /// </summary>
    private static string IncompressibleFiller()
    {
        var noise = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(noise);
        return Convert.ToBase64String(noise);
    }
}
