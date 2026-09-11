using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

using Enlist.ControlPlane.Contracts;
using Enlist.TestSupport;
using Microsoft.Data.SqlClient;

namespace Enlist.ControlPlane.Tests;

/// <summary>
/// GET /health — what a load balancer, the installer's "verify this control plane URL" and the portal's
/// reachability chip get to act on. Proven against the real control plane and the real database: a
/// healthy control plane names itself, its version and its newest applied migration; and a database
/// that goes away AFTER startup turns the answer into a 503 that still names the product (so a caller
/// can tell "enList, degraded" from "not enList"), says why without leaking the connection string, and
/// turns back into a 200 the moment the database returns — no restart needed.
/// </summary>
public sealed class HealthEndpointTests : IAsyncLifetime
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
    public async Task A_healthy_control_plane_names_itself_its_version_and_its_newest_migration()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var health = await response.Content.ReadFromJsonAsync<HealthDto>();
        Assert.NotNull(health);
        Assert.Equal("Healthy", health.Status);
        Assert.Equal("enList control plane", health.Product);
        Assert.False(string.IsNullOrWhiteSpace(health.Version));
        Assert.DoesNotContain("+", health.Version);
        Assert.True(health.Database.Reachable);
        Assert.Null(health.Database.Error);

        // Not merely "some migration": the one the database itself says is newest.
        Assert.Equal(await NewestMigrationInDatabaseAsync(), health.Database.LatestMigration);
    }

    [Fact]
    public async Task A_database_that_goes_away_after_startup_is_a_503_that_still_names_the_product_and_recovers()
    {
        var databaseName = DatabaseName(_server!.ConnectionString);
        await SetDatabaseOnlineAsync(databaseName, online: false);
        try
        {
            var response = await _client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

            var health = await response.Content.ReadFromJsonAsync<HealthDto>();
            Assert.NotNull(health);
            Assert.Equal("Unhealthy", health.Status);
            Assert.Equal("enList control plane", health.Product);
            Assert.False(health.Database.Reachable);
            Assert.Null(health.Database.LatestMigration);
            Assert.False(string.IsNullOrWhiteSpace(health.Database.Error));

            // The reason is a message, never the connection string.
            Assert.DoesNotContain("Trusted_Connection", health.Database.Error);
            Assert.DoesNotContain("Server=", health.Database.Error);
        }
        finally
        {
            await SetDatabaseOnlineAsync(databaseName, online: true);
        }

        // Back to 200 without a restart: the control plane does not cache an unhealthy verdict.
        var recovered = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    private static string DatabaseName(string connectionString) =>
        Regex.Match(connectionString, @"Database=([^;]+)").Groups[1].Value;

    private static string MasterConnectionString(string connectionString) =>
        Regex.Replace(connectionString, @"Database=[^;]+", "Database=master");

    private async Task SetDatabaseOnlineAsync(string databaseName, bool online)
    {
        await using var connection = new SqlConnection(MasterConnectionString(_server!.ConnectionString));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        // ROLLBACK IMMEDIATE severs the control plane's pooled connections too — exactly the outage
        // being simulated, not a polite one it could keep serving from a warm connection.
        command.CommandText = online
            ? $"ALTER DATABASE [{databaseName}] SET ONLINE"
            : $"ALTER DATABASE [{databaseName}] SET OFFLINE WITH ROLLBACK IMMEDIATE";
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string?> NewestMigrationInDatabaseAsync()
    {
        await using var connection = new SqlConnection(_server!.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT TOP 1 MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC";
        return (string?)await command.ExecuteScalarAsync();
    }
}
