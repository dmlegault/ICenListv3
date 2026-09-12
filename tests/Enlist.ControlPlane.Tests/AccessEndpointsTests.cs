using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Enlist.ControlPlane.Contracts;
using Enlist.TestSupport;

namespace Enlist.ControlPlane.Tests;

/// <summary>
/// The management surface for credentials over HTTP (Authentication-Design.md sections 7 and 10) -
/// what the portal's Access page drives - and the audit line every administrative write leaves
/// (6.3). Against a control plane running Required, as an Operator whose key the CLI minted.
/// </summary>
public sealed class AccessEndpointsTests : IAsyncLifetime
{
    private static readonly IReadOnlyDictionary<string, string> Required = new Dictionary<string, string> { ["Authentication__Mode"] = "Required" };

    private ControlPlaneTestServer? _server;
    private HttpClient _http = null!;
    private string _operatorKey = "";

    public async Task InitializeAsync()
    {
        _server = await ControlPlaneTestServer.StartAsync(TimeSpan.FromSeconds(30), Required);
        _http = new HttpClient { BaseAddress = _server.BaseUri };
        _operatorKey = await _server.CreateApiKeyAsync("tests", "Operator");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    [Fact]
    public async Task An_operator_creates_lists_and_revokes_api_keys_and_a_viewer_may_do_none_of_it()
    {
        var created = await SendAsync(HttpMethod.Post, "/api/api-keys", _operatorKey, new CreateApiKeyRequest("dashboard", "Viewer", "7d"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var key = (await created.Content.ReadFromJsonAsync<CreateApiKeyResponse>())!;
        Assert.StartsWith("enlk_", key.Key);
        Assert.Equal("Viewer", key.Role);
        Assert.NotNull(key.ExpiresAtUtc);

        // The new key does what a Viewer does and nothing more - including not touching keys.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/agents", key.Key)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Put, "/api/agents/WEB-30/tags", key.Key, new SetAgentTagsRequest(new Dictionary<string, string>()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/api/api-keys", key.Key)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/api/api-keys", key.Key, new CreateApiKeyRequest("sneaky", "Operator"))).StatusCode);

        // Listed, without the secret; the name is taken while it lives; a bad role is a 400.
        var listed = (await (await SendAsync(HttpMethod.Get, "/api/api-keys", _operatorKey)).Content.ReadFromJsonAsync<List<ApiKeyDto>>())!;
        var dashboard = Assert.Single(listed, k => k.Name == "dashboard");
        Assert.Equal(CredentialStatuses.Live, dashboard.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, "/api/api-keys", _operatorKey, new CreateApiKeyRequest("dashboard", "Viewer"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/api/api-keys", _operatorKey, new CreateApiKeyRequest("x", "Admin"))).StatusCode);

        // Revoked: 401 from then on, shown as revoked, and the name is free again.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, "/api/api-keys/dashboard", _operatorKey)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/agents", key.Key)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, "/api/api-keys/dashboard", _operatorKey)).StatusCode);
        listed = (await (await SendAsync(HttpMethod.Get, "/api/api-keys", _operatorKey)).Content.ReadFromJsonAsync<List<ApiKeyDto>>())!;
        Assert.Equal(CredentialStatuses.Revoked, Assert.Single(listed, k => k.Name == "dashboard").Status);
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, "/api/api-keys", _operatorKey, new CreateApiKeyRequest("dashboard", "Viewer"))).StatusCode);
    }

    [Fact]
    public async Task An_operator_mints_join_tokens_that_enroll_agents_and_can_revoke_them()
    {
        var created = await SendAsync(HttpMethod.Post, "/api/join-tokens", _operatorKey, new CreateJoinTokenRequest("1h", 2));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var token = (await created.Content.ReadFromJsonAsync<CreateJoinTokenResponse>())!;
        Assert.StartsWith("enlj_", token.Token);
        Assert.Equal(2, token.UsesRemaining);

        var enrolled = await SendAsync(HttpMethod.Post, "/api/agents/enroll", token.Token, new EnrollAgentRequest("WEB-31"));
        Assert.Equal(HttpStatusCode.Created, enrolled.StatusCode);

        var listed = (await (await SendAsync(HttpMethod.Get, "/api/join-tokens", _operatorKey)).Content.ReadFromJsonAsync<List<JoinTokenDto>>())!;
        var mine = Assert.Single(listed, t => t.Id == token.Id);
        Assert.Equal(1, mine.UsesRemaining);
        Assert.Equal(CredentialStatuses.Live, mine.Status);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/api/join-tokens/{token.Id}", _operatorKey)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/api/agents/enroll", token.Token, new EnrollAgentRequest("WEB-32"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, $"/api/join-tokens/{token.Id}", _operatorKey)).StatusCode);

        // A join token is meant to be short-lived: never is refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/api/join-tokens", _operatorKey, new CreateJoinTokenRequest("never"))).StatusCode);
    }

    [Fact]
    public async Task Revoking_an_agents_credential_keeps_the_registry_row_and_frees_the_name()
    {
        var joinToken = await _server!.CreateJoinTokenAsync();
        var agentToken = (await (await SendAsync(HttpMethod.Post, "/api/agents/enroll", joinToken, new EnrollAgentRequest("WEB-33"))).Content.ReadFromJsonAsync<EnrollAgentResponse>())!.AgentToken;

        var agent = (await (await SendAsync(HttpMethod.Get, "/api/agents/WEB-33", _operatorKey)).Content.ReadFromJsonAsync<AgentDto>())!;
        Assert.Equal(AgentCredentialStates.Live, agent.CredentialState);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, "/api/agents/WEB-33/credential", _operatorKey)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/agents/WEB-33/policies", agentToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, "/api/agents/WEB-33/credential", _operatorKey)).StatusCode);

        agent = (await (await SendAsync(HttpMethod.Get, "/api/agents/WEB-33", _operatorKey)).Content.ReadFromJsonAsync<AgentDto>())!;
        Assert.Equal(AgentCredentialStates.Revoked, agent.CredentialState);

        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, "/api/agents/enroll", joinToken, new EnrollAgentRequest("WEB-33"))).StatusCode);
        var agents = (await (await SendAsync(HttpMethod.Get, "/api/agents", _operatorKey)).Content.ReadFromJsonAsync<List<AgentDto>>())!;
        Assert.Equal(AgentCredentialStates.Live, Assert.Single(agents, a => a.Name == "WEB-33").CredentialState);
    }

    [Fact]
    public async Task Every_administrative_write_is_audited_with_the_key_and_the_person_and_agent_telemetry_is_not()
    {
        var write = await SendAsync(HttpMethod.Put, "/api/agents/WEB-34/tags", _operatorKey, new SetAgentTagsRequest(new Dictionary<string, string> { ["env"] = "test" }), operatorName: @"CORP\alice");
        Assert.True(write.IsSuccessStatusCode);

        var joinToken = await _server!.CreateJoinTokenAsync();
        var agentToken = (await (await SendAsync(HttpMethod.Post, "/api/agents/enroll", joinToken, new EnrollAgentRequest("WEB-34"))).Content.ReadFromJsonAsync<EnrollAgentResponse>())!.AgentToken;
        var report = await SendAsync(HttpMethod.Post, "/api/agents/WEB-34/report", agentToken, new { generatedAtUtc = DateTimeOffset.UtcNow, applications = Array.Empty<object>() });
        Assert.True(report.IsSuccessStatusCode);

        await WaitForOutputAsync("Audit: POST /api/agents/enroll");
        var output = _server.Output;
        Assert.Contains("Audit: PUT /api/agents/WEB-34/tags -> 200 by key 'tests' for CORP\\alice", output);
        Assert.Contains("Audit: POST /api/agents/enroll -> 201 by join token", output);
        Assert.DoesNotContain("Audit: POST /api/agents/WEB-34/report", output);
    }

    [Fact]
    public async Task A_write_that_throws_is_audited_too_and_says_it_faulted()
    {
        // The line an administrative write leaves when it FAILS is the one most worth having, and
        // until 2026-09-12 it was the only one that was never written: the exception unwound past
        // the logging call and the request vanished from the audit trail entirely.
        //
        // The fault is provoked without relying on any bug: the blob root is removed underneath a
        // running control plane, so the upload's first FileStream has nowhere to go. Put back in the
        // finally, because the rest of this class shares the server.
        var blobRoot = _server!.PackageStorageRoot;
        Directory.Delete(blobRoot, recursive: true);
        try
        {
            var upload = new HttpRequestMessage(HttpMethod.Post, "/api/packages?application=AuditFault")
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 }),
            };
            upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _operatorKey);
            upload.Headers.TryAddWithoutValidation("X-Enlist-Operator", @"CORP\bob");

            var response = await _http.SendAsync(upload);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

            await WaitForOutputAsync("Audit: POST /api/packages");

            // No status code on this path, on purpose: the exception handler upstream has not run
            // yet, so Response.StatusCode is still 200 and printing it would put a lie in the trail.
            Assert.Contains(@"Audit: POST /api/packages -> faulted with DirectoryNotFoundException by key 'tests' for CORP\bob", _server.Output);
        }
        finally
        {
            Directory.CreateDirectory(blobRoot);
        }
    }

    [Fact]
    public async Task A_forged_operator_header_cannot_stretch_or_break_the_audit_line()
    {
        // X-Enlist-Operator is supplied by the caller, so it is attacker-controlled for anyone
        // holding an Operator key, and it lands in both the audit line and a CreatedBy column. It
        // was written through verbatim until 2026-09-12: no length limit at all, so a megabyte of
        // padding went into the log and into nvarchar(max).
        var padded = new string('a', 400);
        var write = await SendAsync(HttpMethod.Put, "/api/agents/WEB-36/tags", _operatorKey, new SetAgentTagsRequest(new Dictionary<string, string> { ["env"] = "test" }), operatorName: padded);
        Assert.True(write.IsSuccessStatusCode);

        await WaitForOutputAsync("Audit: PUT /api/agents/WEB-36/tags");

        Assert.Contains("for " + new string('a', 100), _server!.Output);
        Assert.DoesNotContain(new string('a', 101), _server.Output);
    }

    [Fact]
    public async Task A_newline_in_the_operator_header_never_becomes_a_second_audit_line()
    {
        // Why the middleware strips control characters: the audit trail in v1 IS the log
        // (Authentication-Design.md 6.3), and a log reader splits on newlines, so a CR or LF here
        // would forge whole entries.
        //
        // MEASURED, because it decides how much this test is worth: the HTTP stack truncates the
        // value at the control character, so what reaches the middleware is already just "eve" -
        // the assertions below hold with or without the stripping in AuditIdentity.Sanitize. The
        // stripping stays as defence in depth (the same value reaches a CreatedBy column, and
        // nothing here guarantees every future caller arrives over HTTP/1.1), but the REACHABLE half
        // of this is the length cap, which has its own test above.
        //
        // The WEB-37 assertion is what keeps this honest: it proves the request was processed and
        // audited rather than refused at the door, so the absence of a forged line means something.
        var forgery = "eve\r\nAudit: DELETE /api/api-keys/tests -> 204 by key 'root'";
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/agents/WEB-37/tags")
        {
            Content = JsonContent.Create(new SetAgentTagsRequest(new Dictionary<string, string> { ["env"] = "test" })),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _operatorKey);
        request.Headers.TryAddWithoutValidation("X-Enlist-Operator", forgery);

        await _http.SendAsync(request);
        await WaitForOutputAsync("Audit: PUT /api/agents/WEB-37/tags");

        Assert.Contains("for eve", _server!.Output);
        Assert.DoesNotContain("by key 'root'", _server.Output);
        Assert.DoesNotContain("DELETE /api/api-keys/tests -> 204", _server.Output);

        // Not merely absent as a separate line: absent inline too, which is what would happen if the
        // forgery arrived whole and only the newlines were removed.
        Assert.DoesNotContain("eveAudit", _server.Output);
    }

    private async Task WaitForOutputAsync(string text)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && !_server!.Output.Contains(text))
        {
            await Task.Delay(100);
        }
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
