using System.Net.Http.Json;
using System.Text;

using Enlist.TestSupport;

using Microsoft.Data.SqlClient;

namespace Enlist.ControlPlane.Tests;

/// <summary>
/// Status-report retention on a testable timescale (seconds instead of the real day/hour defaults),
/// against the real control plane and its real database. Two properties matter, and the second is the
/// one that makes the first safe to run unattended: old reports go once a newer one exists, and an
/// agent's newest report is never deleted however old it is — otherwise an agent that has been silent
/// for longer than the window would simply disappear from the portal rather than show its last known
/// state beside an Offline chip.
/// </summary>
public sealed class ReportRetentionTests : IAsyncLifetime
{
    // Retention comfortably longer than the few hundred milliseconds three posts and a count take, and
    // comfortably shorter than the wait that follows — the same margins PackageRetentionTests uses.
    private static readonly Dictionary<string, string> FastRetentionEnv = new()
    {
        ["ReportRetention__SweepInterval"] = "00:00:00.300",
        ["ReportRetention__RetentionPeriod"] = "00:00:02",
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

    [Fact]
    public async Task Reports_older_than_the_retention_period_are_swept_and_the_newest_survives_with_its_content_intact()
    {
        var agent = "agent-" + Guid.NewGuid().ToString("N")[..8];

        for (var n = 1; n <= 3; n++)
        {
            await ReportAsync(agent, $$"""{"n":{{n}}}""");
            await Task.Delay(100);
        }

        Assert.Equal(3, await CountReportsAsync(agent));

        // Past the retention period and several sweeps.
        await Task.Delay(TimeSpan.FromSeconds(5));

        Assert.Equal(1, await CountReportsAsync(agent));
        Assert.Equal("""{"n":3}""", await LatestAsync(agent));
    }

    [Fact]
    public async Task An_agents_only_report_is_kept_no_matter_how_far_past_the_retention_period_it_is()
    {
        var agent = "agent-" + Guid.NewGuid().ToString("N")[..8];
        await ReportAsync(agent, """{"last":"known"}""");

        // Well past retention; the sweep has had every chance to take it.
        await Task.Delay(TimeSpan.FromSeconds(5));

        Assert.Equal(1, await CountReportsAsync(agent));
        Assert.Equal("""{"last":"known"}""", await LatestAsync(agent));
    }

    private async Task ReportAsync(string agent, string snapshotJson)
    {
        var response = await _client.PostAsync($"/api/agents/{agent}/report", new StringContent(snapshotJson, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    private async Task<string> LatestAsync(string agent)
    {
        var response = await _client.GetAsync($"/api/agents/{agent}/report/latest");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>No endpoint lists an agent's reports — that is the point of the table — so the count comes from the database itself.</summary>
    private async Task<int> CountReportsAsync(string agent)
    {
        await using var connection = new SqlConnection(_server!.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM AgentReports WHERE AgentName = @agent";
        command.Parameters.AddWithValue("@agent", agent);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
