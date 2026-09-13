using System.Security.Claims;
using System.Text.Encodings.Web;

using Enlist.ControlPlane.Contracts;
using Enlist.ControlPlane.Data;

using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Enlist.ControlPlane.Authentication;

/// <summary>
/// The one authentication scheme. A bearer token in the Authorization header — or, on the hub path
/// only, the access_token query parameter SignalR's client sends — is hashed and looked up by its kind
/// (prefix) in the matching table; a live one becomes a principal carrying the <see cref="EnlistClaims"/>
/// that <see cref="EndpointPolicyHandler"/> authorizes on. A missing token is "no result", so the
/// authorization layer decides what that means per endpoint; an invalid one is a failure with a reason
/// that names the kind and never the token.
/// </summary>
public sealed class EnlistBearerHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "EnlistBearer";

    /// <summary>LastUsedAtUtc is worth having and not worth a write per request.</summary>
    private static readonly TimeSpan LastUsedGranularity = TimeSpan.FromMinutes(1);

    public EnlistBearerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ReadToken();
        if (token is null)
        {
            return AuthenticateResult.NoResult();
        }

        var db = Context.RequestServices.GetRequiredService<ControlPlaneDbContext>();
        var hash = Tokens.Hash(token);
        var now = DateTimeOffset.UtcNow;
        var identity = new ClaimsIdentity(SchemeName);

        if (token.StartsWith(Tokens.AgentPrefix, StringComparison.Ordinal))
        {
            var credential = await db.AgentCredentials.SingleOrDefaultAsync(c => c.TokenHash == hash, Context.RequestAborted);
            if (credential is null || credential.RevokedAtUtc is not null)
            {
                return Fail("agent credential", credential is null ? "unknown" : "revoked");
            }

            identity.AddClaim(new Claim(EnlistClaims.Kind, EnlistClaims.KindAgent));
            identity.AddClaim(new Claim(EnlistClaims.Agent, credential.AgentName));
            identity.AddClaim(new Claim(EnlistClaims.Credential, credential.AgentName));
            if (Stale(credential.LastUsedAtUtc, now))
            {
                credential.LastUsedAtUtc = now;
                await db.SaveChangesAsync(Context.RequestAborted);
            }
        }
        else if (token.StartsWith(Tokens.ApiKeyPrefix, StringComparison.Ordinal))
        {
            var key = await db.ApiKeys.SingleOrDefaultAsync(k => k.KeyHash == hash, Context.RequestAborted);
            if (key is null || key.RevokedAtUtc is not null || (key.ExpiresAtUtc is { } expires && expires <= now))
            {
                return Fail("API key", key is null ? "unknown" : key.RevokedAtUtc is not null ? "revoked" : "expired");
            }

            identity.AddClaim(new Claim(EnlistClaims.Kind, EnlistClaims.KindManagement));
            identity.AddClaim(new Claim(EnlistClaims.Role, key.Role));
            identity.AddClaim(new Claim(EnlistClaims.Credential, key.Name));
            if (Stale(key.LastUsedAtUtc, now))
            {
                key.LastUsedAtUtc = now;
                await db.SaveChangesAsync(Context.RequestAborted);
            }
        }
        else if (token.StartsWith(Tokens.JoinPrefix, StringComparison.Ordinal))
        {
            var join = await db.JoinTokens.SingleOrDefaultAsync(j => j.TokenHash == hash, Context.RequestAborted);
            if (join is null || join.RevokedAtUtc is not null || join.ExpiresAtUtc <= now || join.UsesRemaining == 0)
            {
                return Fail("join token", join is null ? "unknown" : join.RevokedAtUtc is not null ? "revoked" : join.UsesRemaining == 0 ? "no uses remaining" : "expired");
            }

            identity.AddClaim(new Claim(EnlistClaims.Kind, EnlistClaims.KindJoin));
            identity.AddClaim(new Claim(EnlistClaims.Credential, join.Id.ToString()));
        }
        else
        {
            return Fail("token", "not an enList token");
        }

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }

    private string? ReadToken()
    {
        var header = Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var value = header[7..].Trim();
            return value.Length == 0 ? null : value;
        }

        // SignalR's client cannot set headers on a WebSocket, so it sends the token as a query
        // parameter. Accepted on the hub path and nowhere else, so a token never ends up in the
        // access log of an ordinary request.
        if (Request.Path.StartsWithSegments(ApplicationPolicyHubContract.HubPath) &&
            Request.Query.TryGetValue("access_token", out var fromQuery) &&
            !string.IsNullOrEmpty(fromQuery))
        {
            return fromQuery.ToString();
        }

        return null;
    }

    private AuthenticateResult Fail(string kind, string why)
    {
        Logger.LogInformation("Rejected a {Kind}: {Why} ({Method} {Path}).", kind, why, Request.Method, Request.Path);
        return AuthenticateResult.Fail($"The {kind} presented is {why}.");
    }

    private static bool Stale(DateTimeOffset? lastUsed, DateTimeOffset now) => lastUsed is null || now - lastUsed.Value > LastUsedGranularity;
}
