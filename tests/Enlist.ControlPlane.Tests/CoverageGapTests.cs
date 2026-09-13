using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Enlist.ControlPlane.Contracts;
using Enlist.TestSupport;

using Microsoft.AspNetCore.SignalR.Client;

namespace Enlist.ControlPlane.Tests;

/// <summary>
/// The endpoints and rules that had no test at any tier, found by the 2026-09-12 regression review
/// (section 9 of docs/06-background/enList-v3-Review-2026-09-12.md) and closed here on 2026-09-13.
///
/// Nothing in this file is a new behaviour. Each one is something the system already did, that
/// nothing would have noticed breaking: the portal's Start/Stop path, the log pipeline in both
/// directions, one agent's credential being useless against another's routes, expiry actually
/// expiring, the hub refusing a revoked agent on reconnect, and the audit line for a write that was
/// REFUSED rather than one that succeeded or threw.
///
/// Run against a control plane with authentication Required, because every one of these is a
/// statement about who may do what.
/// </summary>
public sealed class CoverageGapTests : IAsyncLifetime
{
    private ControlPlaneTestServer? _server;
    private HttpClient _http = null!;
    private string _operatorKey = "";
    private string _viewerKey = "";

    public async Task InitializeAsync()
    {
        _server = await ControlPlaneTestServer.StartAsync(TimeSpan.FromSeconds(30), AuthenticationMode.Required);
        _http = new HttpClient { BaseAddress = _server.BaseUri };
        _operatorKey = await _server.CreateApiKeyAsync("gaps-operator", "Operator");
        _viewerKey = await _server.CreateApiKeyAsync("gaps-viewer", "Viewer");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    /// <summary>
    /// The portal's per-service Start/Stop and per-job Enable/Disable buttons all land here, and
    /// nothing tested the endpoint itself. It validates before it pushes, because the push is
    /// fire-and-forget over SignalR - once sent there is nothing left to report a bad TargetKind to.
    /// </summary>
    [Fact]
    public async Task An_imperative_command_is_validated_before_it_is_pushed_and_only_an_operator_may_send_one()
    {
        await EnrollAsync(await _server!.CreateJoinTokenAsync(), "CMD-01");

        var ok = await SendAsync(HttpMethod.Post, "/api/agents/CMD-01/commands", _operatorKey,
            new AgentCommandRequest("Billing", AgentCommandRequest.Service, "Sample Service", AgentCommandRequest.Stop));
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        // Unknown agent: 404, not a silently dropped push. Nothing is listening on that group, and
        // sending to an empty group does not error - so without this check the caller would be told
        // everything went fine.
        var unknown = await SendAsync(HttpMethod.Post, "/api/agents/NOBODY-HERE/commands", _operatorKey,
            new AgentCommandRequest("Billing", AgentCommandRequest.Service, "Sample Service", AgentCommandRequest.Stop));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // Both enumerations are checked, and the message quotes what arrived.
        var badKind = await SendAsync(HttpMethod.Post, "/api/agents/CMD-01/commands", _operatorKey,
            new AgentCommandRequest("Billing", "Sevrice", "Sample Service", AgentCommandRequest.Stop));
        Assert.Equal(HttpStatusCode.BadRequest, badKind.StatusCode);
        Assert.Contains("Sevrice", await badKind.Content.ReadAsStringAsync());

        var badAction = await SendAsync(HttpMethod.Post, "/api/agents/CMD-01/commands", _operatorKey,
            new AgentCommandRequest("Billing", AgentCommandRequest.Service, "Sample Service", "Restart"));
        Assert.Equal(HttpStatusCode.BadRequest, badAction.StatusCode);
        Assert.Contains("Restart", await badAction.Content.ReadAsStringAsync());

        // A Viewer may look at the fleet and may not touch it.
        var refused = await SendAsync(HttpMethod.Post, "/api/agents/CMD-01/commands", _viewerKey,
            new AgentCommandRequest("Billing", AgentCommandRequest.Service, "Sample Service", AgentCommandRequest.Stop));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    /// <summary>
    /// The log pipeline, both ends: an agent POSTs batches as itself, and the portal's tail page GETs
    /// them back paged strictly after the last Id it already has. The paging is the part worth
    /// pinning - a tail that re-sends a line, or skips one, is the whole failure mode.
    /// </summary>
    [Fact]
    public async Task An_agent_forwards_log_batches_and_the_portal_tails_them_strictly_after_the_last_id()
    {
        var agentToken = await EnrollAsync(await _server!.CreateJoinTokenAsync(), "LOG-01");

        var submitted = await SendAsync(HttpMethod.Post, "/api/agents/LOG-01/logs", agentToken,
            new SubmitLogEntriesRequest(
            [
                new LogEntrySubmission("Billing", "Information", "Sample Service", "first", DateTimeOffset.UtcNow),
                new LogEntrySubmission("Billing", "Warning", "Sample Service", "second", DateTimeOffset.UtcNow),
                new LogEntrySubmission("Shipping", "Information", "Other Service", "not billing", DateTimeOffset.UtcNow),
            ]));
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);

        // An empty batch is accepted and writes nothing - the agent's forwarder fires on a timer and
        // a quiet interval must not be an error.
        Assert.Equal(HttpStatusCode.Accepted,
            (await SendAsync(HttpMethod.Post, "/api/agents/LOG-01/logs", agentToken, new SubmitLogEntriesRequest([]))).StatusCode);

        var billing = await ReadLogsAsync("LOG-01", "Billing");
        Assert.Equal(["first", "second"], billing.Select(e => e.Text));
        Assert.Equal("Warning", billing[1].Level);

        // Filtered by application, not just by agent.
        Assert.Equal(["not billing"], (await ReadLogsAsync("LOG-01", "Shipping")).Select(e => e.Text));

        // sinceId is exclusive, which is what makes a tail continuous rather than repeating.
        var afterFirst = await ReadLogsAsync("LOG-01", "Billing", sinceId: billing[0].Id);
        Assert.Equal(["second"], afterFirst.Select(e => e.Text));
        Assert.Empty(await ReadLogsAsync("LOG-01", "Billing", sinceId: billing[^1].Id));

        // take bounds the page.
        Assert.Single(await ReadLogsAsync("LOG-01", "Billing", take: 1));

        // Reading is a Viewer's right; writing as an agent is not a Viewer's.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(HttpMethod.Post, "/api/agents/LOG-01/logs", _viewerKey, new SubmitLogEntriesRequest([]))).StatusCode);
    }

    /// <summary>
    /// An agent credential is not a fleet-wide pass. Every agent-tier route binds the name in the URL
    /// to the name in the credential, and this is the test that the binding is actually applied
    /// rather than merely written down in EndpointPolicies.
    /// </summary>
    [Fact]
    public async Task One_agents_credential_is_refused_on_another_agents_routes()
    {
        var joinToken = await _server!.CreateJoinTokenAsync("--uses", "2");
        var mine = await EnrollAsync(joinToken, "PAIR-A");
        await EnrollAsync(joinToken, "PAIR-B");

        // Its own routes: fine.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/agents/PAIR-A/policies", mine)).StatusCode);

        // Its neighbour's: refused, and 403 rather than 404 - the credential is valid, the request is
        // not permitted, and pretending PAIR-B does not exist would send whoever is debugging it
        // looking for a registration problem that is not there.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/api/agents/PAIR-B/policies", mine)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(HttpMethod.Post, "/api/agents/PAIR-B/report", mine, new { generatedAtUtc = DateTimeOffset.UtcNow, applications = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(HttpMethod.Post, "/api/agents/PAIR-B/logs", mine, new SubmitLogEntriesRequest([]))).StatusCode);

        // The name comparison is case-insensitive, because that is how the database compares agent
        // names - so this is the SAME agent, not a near miss that happens to be allowed.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/agents/pair-a/policies", mine)).StatusCode);

        // And it is AUDITED. An agent acting as itself is telemetry and deliberately not audited, but
        // a valid credential used against a DIFFERENT agent is rare, never accidental, and the one
        // agent-shaped event worth a line. PAIR-A's own report above leaves none, which is what makes
        // the presence of this one mean something.
        await Poll.UntilAsync(
            () => _server.Output.Contains("Audit: POST /api/agents/PAIR-B/logs"),
            TimeSpan.FromSeconds(10),
            "one agent reaching for another agent's route left no audit line.");
        Assert.Contains("Audit: POST /api/agents/PAIR-B/logs -> 403 by agent 'PAIR-A'", _server.Output);
    }

    /// <summary>
    /// Expiry was stored, listed and reported, and nothing proved it was ever ENFORCED. It cannot be
    /// waited out: the shortest expiry any surface accepts is an hour, deliberately. So the row is
    /// aged instead - the one thing these tests reach around the API for, and why
    /// ControlPlaneTestServer.ExecuteSqlAsync exists.
    /// </summary>
    [Fact]
    public async Task An_expired_key_and_an_expired_join_token_are_both_refused()
    {
        var key = await _server!.CreateApiKeyAsync("short-lived", "Viewer", "--expires", "1h");
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/agents", key)).StatusCode);

        Assert.Equal(1, await _server.ExecuteSqlAsync(
            "UPDATE ApiKeys SET ExpiresAtUtc = DATEADD(minute, -1, SYSDATETIMEOFFSET()) WHERE Name = 'short-lived'"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/agents", key)).StatusCode);

        // Expired is not the same state as revoked, and the listing says which.
        Assert.Matches(@"short-lived\s+Viewer.*expired", await _server.RunCliAsync("list-keys"));

        var joinToken = await _server.CreateJoinTokenAsync("--expires", "1h");
        Assert.Equal(1, await _server.ExecuteSqlAsync(
            "UPDATE JoinTokens SET ExpiresAtUtc = DATEADD(minute, -1, SYSDATETIMEOFFSET())"));

        var enrolled = await SendAsync(HttpMethod.Post, "/api/agents/enroll", joinToken, new EnrollAgentRequest("TOO-LATE"));
        Assert.Equal(HttpStatusCode.Unauthorized, enrolled.StatusCode);

        // And it did not half-enroll on the way to being refused.
        var agents = (await (await SendAsync(HttpMethod.Get, "/api/agents", _operatorKey)).Content.ReadFromJsonAsync<List<AgentDto>>())!;
        Assert.DoesNotContain(agents, a => a.Name == "TOO-LATE");
    }

    /// <summary>
    /// Revocation has to reach the hub, not only the REST surface. An agent whose credential is
    /// revoked while it is connected keeps that connection until something disturbs it - SignalR does
    /// not re-authenticate an established one - and the moment that matters is the RECONNECT, which
    /// is exactly when nobody is watching.
    /// </summary>
    [Fact]
    public async Task A_revoked_agent_cannot_reconnect_to_the_hub()
    {
        var agentToken = await EnrollAsync(await _server!.CreateJoinTokenAsync(), "HUB-01");
        var hubUrl = new Uri(_server.BaseUri, ApplicationPolicyHubContract.HubPath.TrimStart('/'));

        await using (var connected = Build(hubUrl, agentToken))
        {
            await connected.StartAsync();
            await connected.InvokeAsync("JoinAgentGroup", "HUB-01");
        }

        Assert.Equal(HttpStatusCode.NoContent,
            (await SendAsync(HttpMethod.Delete, "/api/agents/HUB-01/credential", _operatorKey)).StatusCode);

        await using var reconnecting = Build(hubUrl, agentToken);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => reconnecting.StartAsync());
    }

    /// <summary>
    /// The audit trail has a line for a write that succeeded and a line for one that threw. A write
    /// that was REFUSED had neither test nor, until this was checked, any certainty that it produced
    /// one - and an attempt to do something one is not allowed to do is the single most interesting
    /// thing in an audit trail.
    /// </summary>
    [Fact]
    public async Task A_refused_write_is_audited_naming_who_tried_it()
    {
        var refused = await SendAsync(HttpMethod.Put, "/api/agents/AUDIT-403/tags", _viewerKey,
            new SetAgentTagsRequest(new Dictionary<string, string> { ["env"] = "test" }), operatorName: @"CORP\mallory");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        await Poll.UntilAsync(
            () => _server!.Output.Contains("Audit: PUT /api/agents/AUDIT-403/tags"),
            TimeSpan.FromSeconds(10),
            "a refused administrative write left no audit line at all.");

        // The status is in the line, so a refusal is distinguishable from a success at a glance, and
        // the key that tried it is named - which is the whole point of auditing a refusal.
        Assert.Contains(@"Audit: PUT /api/agents/AUDIT-403/tags -> 403 by key 'gaps-viewer' for CORP\mallory", _server!.Output);
    }

    private static HubConnection Build(Uri hubUrl, string token) =>
        new HubConnectionBuilder()
            .WithUrl(hubUrl, options => options.AccessTokenProvider = () => Task.FromResult<string?>(token))
            .Build();

    private async Task<string> EnrollAsync(string joinToken, string agentName)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/agents/enroll", joinToken, new EnrollAgentRequest(agentName));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EnrollAgentResponse>())!.AgentToken;
    }

    private async Task<List<AgentLogEntryDto>> ReadLogsAsync(string agentName, string application, long? sinceId = null, int? take = null)
    {
        var query = $"/api/agents/{agentName}/logs?application={application}";
        if (sinceId is { } id)
        {
            query += $"&sinceId={id}";
        }

        if (take is { } count)
        {
            query += $"&take={count}";
        }

        var response = await SendAsync(HttpMethod.Get, query, _viewerKey);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<AgentLogEntryDto>>())!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string bearer, object? body = null, string? operatorName = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (operatorName is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Enlist-Operator", operatorName);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _http.SendAsync(request);
    }
}
