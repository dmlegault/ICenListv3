using Enlist.ControlPlane.Contracts;
using Enlist.ControlPlane.Data;

using Microsoft.EntityFrameworkCore;

namespace Enlist.ControlPlane.Authentication;

/// <summary>
/// Key and join-token management over HTTP — what the portal's Access page and the Agents tab's
/// "Enroll agent" drive (Authentication-Design.md section 7, last row: Operator only, listing
/// included). The same <see cref="CredentialIssuer"/> the CLI verbs use, so the two doors cannot
/// disagree about validation, defaults or status words. CreatedBy records the key and, when the
/// portal says who is behind it, the person.
/// </summary>
public static class AccessEndpoints
{
    public static void MapAccessEndpoints(this WebApplication app)
    {
        app.MapPost("/api/api-keys", async (CreateApiKeyRequest request, HttpContext http, ControlPlaneDbContext db) =>
        {
            try
            {
                var (entity, key) = await CredentialIssuer.CreateApiKeyAsync(db, request.Name, request.Role, request.ExpiresIn, AuditIdentity.Describe(http), http.RequestAborted);
                return Results.Created($"/api/api-keys/{Uri.EscapeDataString(entity.Name)}", new CreateApiKeyResponse(entity.Id, entity.Name, entity.Role, entity.ExpiresAtUtc, key));
            }
            catch (CredentialRequestException ex)
            {
                return ex.Conflict ? Results.Conflict(ex.Message) : Results.BadRequest(ex.Message);
            }
        });

        app.MapGet("/api/api-keys", async (ControlPlaneDbContext db) =>
        {
            var now = DateTimeOffset.UtcNow;
            var keys = await db.ApiKeys.OrderBy(k => k.Name).ThenByDescending(k => k.CreatedAtUtc).ToListAsync();
            return Results.Ok(keys.Select(k => CredentialIssuer.ToDto(k, now)));
        });

        app.MapDelete("/api/api-keys/{name}", async (string name, ControlPlaneDbContext db) =>
            await CredentialIssuer.RevokeApiKeyAsync(db, name) ? Results.NoContent() : Results.NotFound($"No live API key named '{name}'."));

        app.MapPost("/api/join-tokens", async (CreateJoinTokenRequest request, HttpContext http, ControlPlaneDbContext db) =>
        {
            try
            {
                var (entity, token) = await CredentialIssuer.CreateJoinTokenAsync(db, request.ExpiresIn, request.Uses, AuditIdentity.Describe(http), http.RequestAborted);
                return Results.Created($"/api/join-tokens/{entity.Id}", new CreateJoinTokenResponse(entity.Id, entity.ExpiresAtUtc, entity.UsesRemaining, token));
            }
            catch (CredentialRequestException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        app.MapGet("/api/join-tokens", async (ControlPlaneDbContext db) =>
        {
            var now = DateTimeOffset.UtcNow;
            var tokens = await db.JoinTokens.OrderByDescending(t => t.CreatedAtUtc).ToListAsync();
            return Results.Ok(tokens.Select(t => CredentialIssuer.ToDto(t, now)));
        });

        app.MapDelete("/api/join-tokens/{id:guid}", async (Guid id, ControlPlaneDbContext db) =>
            await CredentialIssuer.RevokeJoinTokenAsync(db, id) ? Results.NoContent() : Results.NotFound("No live join token with that id."));

        // The Agents tab's "Revoke credential": the agent keeps running what it runs, its next call is
        // refused, and the name is free to enroll again. DELETE /api/agents/{name} does this too, as
        // part of deregistering; this is the version that keeps the registry row.
        app.MapDelete("/api/agents/{name}/credential", async (string name, ControlPlaneDbContext db) =>
            await CredentialIssuer.RevokeAgentCredentialAsync(db, name) ? Results.NoContent() : Results.NotFound($"Agent '{name}' holds no live credential."));
    }
}
