using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Enlist.ControlPlane.Contracts;
using Enlist.TestSupport;

using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace Enlist.ControlPlane.Tests;

/// <summary>
/// Review finding C2, step 1 (docs/03-architecture/Authentication-Design.md §13): the control plane
/// under Authentication:Mode=Required, driven exactly as an operator and an agent would drive it —
/// the management verbs on the executable to mint credentials, then HTTP and the real SignalR client
/// with those credentials. What is pinned: nothing but /health answers an anonymous caller; a join
/// token enrolls an agent once and the credential it yields is bound to that name and nothing else;
/// a name cannot be enrolled twice; keys carry roles; revocation is immediate; the hub refuses another
/// agent's group; and the two startup rules refuse the configurations they exist to refuse.
/// </summary>
public sealed class AuthenticationTests : IAsyncLifetime
{
    private static readonly TimeSpan Ready = TimeSpan.FromSeconds(30);
    private static readonly IReadOnlyDictionary<string, string> Required = AuthenticationMode.Required;

    private ControlPlaneTestServer? _server;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _server = await ControlPlaneTestServer.StartAsync(Ready, Required);
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
    public async Task Nothing_but_health_answers_an_anonymous_caller_and_health_says_authentication_is_required()
    {
        var health = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("Required", (await health.Content.ReadFromJsonAsync<HealthDto>())!.Authentication);

        foreach (var path in new[] { "/api/agents", "/api/packages", "/api/application-policies", "/api/agents/WEB-07/policies", "/api/endpoints" })
        {
            var response = await _client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
        }

        // A token of the right shape that nobody issued is refused the same way.
        using var bogus = Request(HttpMethod.Get, "/api/agents", "enlk_" + new string('A', 43));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(bogus)).StatusCode);
    }

    [Fact]
    public async Task A_join_token_enrolls_an_agent_once_and_the_credential_is_bound_to_that_name()
    {
        var joinToken = await _server!.CreateJoinTokenAsync("--uses", "1");

        var enrolled = await EnrollAsync(joinToken, "WEB-07");
        Assert.Equal(HttpStatusCode.Created, enrolled.StatusCode);
        var credential = (await enrolled.Content.ReadFromJsonAsync<EnrollAgentResponse>())!;
        Assert.Equal("WEB-07", credential.AgentName);
        Assert.StartsWith("enla_", credential.AgentToken);

        // Single use: the same join token is now refused outright.
        Assert.Equal(HttpStatusCode.Unauthorized, (await EnrollAsync(joinToken, "WEB-08")).StatusCode);

        // As itself: the agent-facing endpoints answer.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/agents/WEB-07/policies", credential.AgentToken)).StatusCode);

        // As anyone else: forbidden — the name in the URL must be the name in the credential.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/api/agents/WEB-08/policies", credential.AgentToken)).StatusCode);

        // An agent is not an operator: it cannot upload code, and it cannot read the registry.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/api/packages?application=X", credential.AgentToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/api/agents", credential.AgentToken)).StatusCode);

        // But it may download packages — 404 here, because there is none, is an authorized answer.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/api/packages/" + new string('0', 64), credential.AgentToken)).StatusCode);

        // The join token was good for exactly one enrollment, and the ledger says so.
        Assert.Contains("used up", await _server.RunCliAsync("list-join-tokens"));
    }

    [Fact]
    public async Task A_name_that_already_holds_a_credential_cannot_be_enrolled_again_until_it_is_revoked()
    {
        var joinToken = await _server!.CreateJoinTokenAsync("--uses", "3");

        var first = await EnrollAsync(joinToken, "WEB-09");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstToken = (await first.Content.ReadFromJsonAsync<EnrollAgentResponse>())!.AgentToken;

        // The failure the Runbook records — two agents with one name — is now refused at the door.
        var duplicate = await EnrollAsync(joinToken, "WEB-09");
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Contains("already holds a live credential", await duplicate.Content.ReadAsStringAsync());

        // Revoke, then the name is free again, and the old credential is dead the moment it is revoked.
        await _server.RunCliAsync("revoke-agent", "--name", "WEB-09");
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/agents/WEB-09/policies", firstToken)).StatusCode);

        var again = await EnrollAsync(joinToken, "WEB-09");
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        var secondToken = (await again.Content.ReadFromJsonAsync<EnrollAgentResponse>())!.AgentToken;
        Assert.NotEqual(firstToken, secondToken);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/agents/WEB-09/policies", secondToken)).StatusCode);
    }

    [Fact]
    public async Task API_keys_carry_a_role_and_deleting_an_agent_revokes_its_credential()
    {
        var viewer = await _server!.CreateApiKeyAsync("dashboard", "Viewer");
        var operatorKey = await _server.CreateApiKeyAsync("ops", "Operator", "--expires", "never");

        // Viewer: reads yes, writes no.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/agents", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Put, "/api/agents/WEB-10/tags", viewer, new SetAgentTagsRequest(new Dictionary<string, string>()))).StatusCode);

        // Operator: the write goes through (whatever success code the endpoint uses; the point is that it is not 401 or 403).
        Assert.True((await SendAsync(HttpMethod.Put, "/api/agents/WEB-10/tags", operatorKey, new SetAgentTagsRequest(new Dictionary<string, string> { ["env"] = "test" }))).IsSuccessStatusCode);

        // Enroll WEB-10, then delete it as an Operator: the credential goes with the agent (cascade),
        // so DELETE is a revocation with no second step.
        var joinToken = await _server.CreateJoinTokenAsync();
        var agentToken = (await (await EnrollAsync(joinToken, "WEB-10")).Content.ReadFromJsonAsync<EnrollAgentResponse>())!.AgentToken;
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/agents/WEB-10/policies", agentToken)).StatusCode);

        Assert.True((await SendAsync(HttpMethod.Delete, "/api/agents/WEB-10", operatorKey)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/agents/WEB-10/policies", agentToken)).StatusCode);

        // A revoked key is dead immediately too, and stays listed as revoked.
        await _server.RunCliAsync("revoke-api-key", "--name", "dashboard");
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/agents", viewer)).StatusCode);
        Assert.Matches(@"dashboard\s+Viewer.*revoked", await _server.RunCliAsync("list-keys"));
    }

    [Fact]
    public async Task The_hub_admits_an_agent_to_its_own_group_only_and_nobody_without_a_credential()
    {
        var joinToken = await _server!.CreateJoinTokenAsync();
        var agentToken = (await (await EnrollAsync(joinToken, "WEB-11")).Content.ReadFromJsonAsync<EnrollAgentResponse>())!.AgentToken;
        var hubUrl = new Uri(_server.BaseUri, ApplicationPolicyHubContract.HubPath.TrimStart('/'));

        // No credential: the connection is refused at negotiate.
        await using (var anonymous = new HubConnectionBuilder().WithUrl(hubUrl).Build())
        {
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => anonymous.StartAsync());
        }

        // The agent's own credential: connects, joins its own group, and is refused every other group.
        await using var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options => options.AccessTokenProvider = () => Task.FromResult<string?>(agentToken))
            .Build();
        await connection.StartAsync();

        await connection.InvokeAsync("JoinAgentGroup", "WEB-11");
        var refused = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("JoinAgentGroup", "WEB-12"));
        Assert.Contains("WEB-12", refused.Message);
    }

    [Fact]
    public async Task Authentication_cannot_be_turned_off_on_an_address_that_is_not_loopback()
    {
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ControlPlaneTestServer.StartAsync(Ready, AuthenticationMode.Off, listenUrls: "http://0.0.0.0:0"));
        Assert.Contains("off loopback", refused.Message);
        Assert.Contains("no override", refused.Message);
    }

    [Fact]
    public async Task Authentication_cannot_be_turned_off_on_a_port_variable_either()
    {
        // The container case. No --urls at all, so the listener comes from ASPNETCORE_HTTP_PORTS -
        // which the official ASP.NET Core images set to 8080 themselves - and a bare port list binds
        // every interface. ListenerRules read only --urls and Kestrel:Endpoints until 2026-09-12, so
        // this exact configuration started an unauthenticated control plane on the network and the
        // hard rule said nothing. Port 0 keeps the test off any real port: it is refused before
        // Kestrel ever binds.
        var off = new Dictionary<string, string>
        {
            ["Authentication__Mode"] = "Off",
            ["ASPNETCORE_HTTP_PORTS"] = "8080",
        };

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => ControlPlaneTestServer.StartAsync(Ready, off, listenUrls: ""));
        Assert.Contains("off loopback", refused.Message);
        Assert.Contains("8080", refused.Message);
    }

    [Fact]
    public async Task Required_refuses_plain_http_on_an_address_that_is_not_loopback()
    {
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => ControlPlaneTestServer.StartAsync(Ready, Required, listenUrls: "http://0.0.0.0:0"));
        Assert.Contains("plain HTTP off loopback", refused.Message);
    }

    [Fact]
    public async Task Off_on_loopback_is_anonymous_exactly_as_before_and_says_so()
    {
        // The default the rest of the suite (and the demo) runs under.
        await using var server = await ControlPlaneTestServer.StartAsync(Ready);
        using var client = new HttpClient { BaseAddress = server.BaseUri };

        Assert.Equal("Off", (await client.GetFromJsonAsync<HealthDto>("/health"))!.Authentication);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/agents")).StatusCode);
    }

    private Task<HttpResponseMessage> EnrollAsync(string joinToken, string agentName) =>
        SendAsync(HttpMethod.Post, "/api/agents/enroll", joinToken, new EnrollAgentRequest(agentName));

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string bearer, object? body = null)
    {
        using var request = Request(method, path, bearer);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string bearer)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return request;
    }
}
