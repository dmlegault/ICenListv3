using System.Net.Http.Json;

using Enlist.ControlPlane.Contracts;
using Enlist.TestSupport;

using Microsoft.Data.SqlClient;

namespace Enlist.ControlPlane.Tests;

/// <summary>
/// The manifest is a fact about the bytes, so it is scanned once — at upload — and served from the
/// package row. Extracting the zip and reflecting over every DLL on every request was the cost the
/// Applications page paid once per digest on every refresh. A row from before the column existed is
/// scanned on its first request and kept.
/// </summary>
public sealed class PackageManifestTests : IAsyncLifetime
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
    public async Task The_manifest_is_scanned_at_upload_and_served_from_the_stored_row()
    {
        var digest = await UploadSampleAsync();

        var stored = await StoredManifestAsync(digest);
        Assert.NotNull(stored);
        Assert.Contains("Sample Service", stored);

        var served = await (await _client.GetAsync($"/api/packages/{digest}/manifest")).EnsureSuccessStatusCode().Content.ReadAsStringAsync();
        Assert.Contains("Sample Service", served);
        Assert.Contains("Sample Job", served);
    }

    [Fact]
    public async Task A_row_from_before_the_column_existed_is_scanned_on_first_request_and_kept()
    {
        var digest = await UploadSampleAsync();
        await ClearStoredManifestAsync(digest);
        Assert.Null(await StoredManifestAsync(digest));

        var served = await (await _client.GetAsync($"/api/packages/{digest}/manifest")).EnsureSuccessStatusCode().Content.ReadAsStringAsync();
        Assert.Contains("Sample Service", served);

        Assert.NotNull(await StoredManifestAsync(digest));
    }

    private async Task<string> UploadSampleAsync()
    {
        var response = await _client.PostAsync("/api/packages", new ByteArrayContent(TestPackaging.ZipDirectory(RepoPaths.SampleServiceDir())));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UploadPackageResponse>())!.Digest;
    }

    private async Task<string?> StoredManifestAsync(string digest)
    {
        await using var connection = new SqlConnection(_server!.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ManifestJson FROM Packages WHERE Digest = @digest";
        command.Parameters.AddWithValue("@digest", digest);
        return await command.ExecuteScalarAsync() as string;
    }

    private async Task ClearStoredManifestAsync(string digest)
    {
        await using var connection = new SqlConnection(_server!.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Packages SET ManifestJson = NULL WHERE Digest = @digest";
        command.Parameters.AddWithValue("@digest", digest);
        await command.ExecuteNonQueryAsync();
    }
}
