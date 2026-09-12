using System.Globalization;
using System.Text.RegularExpressions;

using Enlist.ControlPlane.Contracts;
using Enlist.ControlPlane.Data;

using Microsoft.EntityFrameworkCore;

namespace Enlist.ControlPlane.Authentication;

/// <summary>A request the issuer refuses, in words the caller can show. Conflict distinguishes "that name is taken" (409) from "that is not a valid request" (400).</summary>
public sealed class CredentialRequestException : Exception
{
    public CredentialRequestException(string message, bool conflict = false)
        : base(message)
    {
        Conflict = conflict;
    }

    public bool Conflict { get; }
}

/// <summary>
/// The one place a management credential is minted or revoked, behind both doors: the CLI verbs on
/// the control plane executable (bootstrap, no HTTP credential) and the Operator-only endpoints the
/// portal's Access page calls. Same validation, same defaults, same status words, whichever door.
/// Every secret is returned once to the caller and only its SHA-256 is stored.
/// </summary>
public static class CredentialIssuer
{
    public static readonly TimeSpan DefaultApiKeyLifetime = TimeSpan.FromDays(90);
    public static readonly TimeSpan DefaultJoinTokenLifetime = TimeSpan.FromHours(24);

    public static async Task<(ApiKeyEntity Entity, string Key)> CreateApiKeyAsync(ControlPlaneDbContext db, string? name, string? role, string? expiresIn, string createdBy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new CredentialRequestException("An API key needs a name (ci-main, portal, dashboard).");
        }

        if (!ManagementRoles.IsValid(role))
        {
            throw new CredentialRequestException($"Role must be {ManagementRoles.Operator} or {ManagementRoles.Viewer}.");
        }

        name = name.Trim();
        if (await db.ApiKeys.AnyAsync(k => k.Name == name && k.RevokedAtUtc == null, ct).ConfigureAwait(false))
        {
            throw new CredentialRequestException($"An API key named '{name}' already exists. Revoke it first, or pick another name.", conflict: true);
        }

        var now = DateTimeOffset.UtcNow;
        var expires = Expiry.Parse(expiresIn, DefaultApiKeyLifetime, now);
        var key = Tokens.Generate(Tokens.ApiKeyPrefix);
        var entity = new ApiKeyEntity
        {
            Id = Guid.NewGuid(),
            Name = name,
            KeyHash = Tokens.Hash(key),
            Role = role!,
            CreatedBy = createdBy,
            CreatedAtUtc = now,
            ExpiresAtUtc = expires,
        };
        db.ApiKeys.Add(entity);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // The check above is a courtesy that produces the good message in the ordinary case; the
            // filtered unique index on live names is the actual guarantee, and two callers creating
            // the same name at once BOTH pass the check. The loser used to surface as a 500 for the
            // one condition this method documents as a 409.
            db.Entry(entity).State = EntityState.Detached;

            // Ask the database whether it really is a name clash, rather than reading a
            // provider-specific error number: a genuine failure must not be relabelled as a taken name.
            if (await db.ApiKeys.AnyAsync(k => k.Name == name && k.RevokedAtUtc == null, ct).ConfigureAwait(false))
            {
                throw new CredentialRequestException($"An API key named '{name}' already exists. Revoke it first, or pick another name.", conflict: true);
            }

            throw;
        }

        return (entity, key);
    }

    public static async Task<(JoinTokenEntity Entity, string Token)> CreateJoinTokenAsync(ControlPlaneDbContext db, string? expiresIn, int? uses, string createdBy, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = Expiry.Parse(expiresIn, DefaultJoinTokenLifetime, now)
            ?? throw new CredentialRequestException("A join token cannot be created with expiry 'never'; it is meant to be short-lived.");

        if (uses is < 1)
        {
            throw new CredentialRequestException("Uses must be a positive whole number, or unset for unlimited until expiry.");
        }

        var token = Tokens.Generate(Tokens.JoinPrefix);
        var entity = new JoinTokenEntity
        {
            Id = Guid.NewGuid(),
            TokenHash = Tokens.Hash(token),
            CreatedBy = createdBy,
            CreatedAtUtc = now,
            ExpiresAtUtc = expires,
            UsesRemaining = uses,
        };
        db.JoinTokens.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (entity, token);
    }

    /// <summary>False when no live key has that name.</summary>
    public static async Task<bool> RevokeApiKeyAsync(ControlPlaneDbContext db, string name, CancellationToken ct = default)
    {
        var key = await db.ApiKeys.SingleOrDefaultAsync(k => k.Name == name && k.RevokedAtUtc == null, ct).ConfigureAwait(false);
        if (key is null)
        {
            return false;
        }

        key.RevokedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>False when the token is unknown or already revoked.</summary>
    public static async Task<bool> RevokeJoinTokenAsync(ControlPlaneDbContext db, Guid id, CancellationToken ct = default)
    {
        var token = await db.JoinTokens.FindAsync([id], ct).ConfigureAwait(false);
        if (token is null || token.RevokedAtUtc is not null)
        {
            return false;
        }

        token.RevokedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>False when the agent holds no live credential. The agent keeps running what it runs (Authentication-Design.md 4.4) and can re-enroll with a new join token.</summary>
    public static async Task<bool> RevokeAgentCredentialAsync(ControlPlaneDbContext db, string agentName, CancellationToken ct = default)
    {
        var credential = await db.AgentCredentials.SingleOrDefaultAsync(c => c.AgentName == agentName && c.RevokedAtUtc == null, ct).ConfigureAwait(false);
        if (credential is null)
        {
            return false;
        }

        credential.RevokedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public static string StatusOf(ApiKeyEntity key, DateTimeOffset now) =>
        key.RevokedAtUtc is not null ? CredentialStatuses.Revoked
        : key.ExpiresAtUtc is { } expires && expires <= now ? CredentialStatuses.Expired
        : CredentialStatuses.Live;

    public static string StatusOf(JoinTokenEntity token, DateTimeOffset now) =>
        token.RevokedAtUtc is not null ? CredentialStatuses.Revoked
        : token.ExpiresAtUtc <= now ? CredentialStatuses.Expired
        : token.UsesRemaining == 0 ? CredentialStatuses.UsedUp
        : CredentialStatuses.Live;

    public static ApiKeyDto ToDto(ApiKeyEntity key, DateTimeOffset now) =>
        new(key.Id, key.Name, key.Role, key.CreatedBy, key.CreatedAtUtc, key.ExpiresAtUtc, key.LastUsedAtUtc, key.RevokedAtUtc, StatusOf(key, now));

    public static JoinTokenDto ToDto(JoinTokenEntity token, DateTimeOffset now) =>
        new(token.Id, token.CreatedBy, token.CreatedAtUtc, token.ExpiresAtUtc, token.UsesRemaining, token.RevokedAtUtc, StatusOf(token, now));

    /// <summary>"24h", "90d" or "never" — the one spelling the CLI, the API and the portal all accept.</summary>
    public static class Expiry
    {
        /// <summary>Null means never; the default lifetime applies when the text is empty.</summary>
        public static DateTimeOffset? Parse(string? text, TimeSpan defaultLifetime, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return now + defaultLifetime;
            }

            if (text.Trim().Equals("never", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var match = Regex.Match(text.Trim(), @"^(\d+)([hd])$", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                throw new CredentialRequestException($"Expiry must be like 24h, 7d or never, got '{text}'.");
            }

            var amount = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            return now + (match.Groups[2].Value.Equals("h", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromHours(amount) : TimeSpan.FromDays(amount));
        }
    }
}
