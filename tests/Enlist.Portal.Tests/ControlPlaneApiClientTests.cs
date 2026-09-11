using System.Net;
using System.Security.Claims;

using Enlist.Portal.Services;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace Enlist.Portal.Tests;

/// <summary>
/// What the portal puts on every call to the control plane, and what it refuses to send at all
/// (Authentication-Design.md 6.1): the portal's key as the bearer, the person's Windows identity in
/// X-Enlist-Operator, and - because that key is Operator-tier whoever is looking at the page - a
/// Viewer's write refused here, before any bytes leave.
/// </summary>
public sealed class ControlPlaneApiClientTests
{
    [Fact]
    public async Task Every_request_carries_the_portal_key_and_names_the_person()
    {
        var handler = new CapturingHandler();
        var client = Client(handler, "enlk_portal", User(@"CORP\alice", PortalRoles.Operator));

        await client.GetAgentsAsync();

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("Bearer", sent.Headers.Authorization?.Scheme);
        Assert.Equal("enlk_portal", sent.Headers.Authorization?.Parameter);
        Assert.Equal(@"CORP\alice", Assert.Single(sent.Headers.GetValues(ControlPlaneApiClient.OperatorHeader)));
    }

    [Fact]
    public async Task A_viewers_write_is_refused_before_it_leaves_and_reads_still_go()
    {
        var handler = new CapturingHandler();
        var client = Client(handler, "enlk_portal", User(@"CORP\bob", PortalRoles.Viewer));

        await client.GetAgentsAsync();
        Assert.Single(handler.Requests);

        var refused = await Assert.ThrowsAsync<ApiException>(() => client.SetAgentTagsAsync("WEB-07", new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("Operator", refused.Message);
        Assert.Single(handler.Requests);   // the PUT never went out
    }

    [Fact]
    public async Task With_no_key_and_no_person_the_call_is_the_anonymous_one_it_always_was()
    {
        // The demo's shape: authentication Off on both sides, nothing to present, nobody to name.
        var handler = new CapturingHandler();
        var client = new ControlPlaneApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        await client.SetAgentTagsAsync("WEB-07", new Dictionary<string, string>());

        var sent = Assert.Single(handler.Requests);
        Assert.Null(sent.Headers.Authorization);
        Assert.False(sent.Headers.Contains(ControlPlaneApiClient.OperatorHeader));
    }

    private static ControlPlaneApiClient Client(HttpMessageHandler handler, string key, ClaimsPrincipal user) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, new PortalCredential(key), new FixedAuthenticationState(user), new RoleAuthorization());

    private static ClaimsPrincipal User(string name, params string[] roles)
    {
        var identity = new ClaimsIdentity("Negotiate", ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.Name, name));
        foreach (var role in roles)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(identity);
    }

    private sealed class FixedAuthenticationState(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }

    /// <summary>The portal's real policies are RequireRole(policy name); this is that, without a service container.</summary>
    private sealed class RoleAuthorization : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Success());

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(user.IsInRole(policyName) ? AuthorizationResult.Success() : AuthorizationResult.Failed());
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var body = request.Method == HttpMethod.Get ? "[]" : """{"name":"WEB-07","tags":{},"firstSeenUtc":"2026-09-11T00:00:00Z","lastSeenUtc":"2026-09-11T00:00:00Z"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
