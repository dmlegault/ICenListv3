namespace Enlist.ControlPlane.Contracts;

/// <summary>
/// The management surface for credentials (Authentication-Design.md sections 7 and 10): what the
/// portal's Access page and the CLI verbs both drive. Every create returns its secret exactly once,
/// in the response and nowhere else; the list DTOs never carry it.
/// </summary>
public sealed record CreateApiKeyRequest(string Name, string Role, string? ExpiresIn = null);

/// <summary>Key is shown here and never again.</summary>
public sealed record CreateApiKeyResponse(Guid Id, string Name, string Role, DateTimeOffset? ExpiresAtUtc, string Key);

public sealed record ApiKeyDto(
    Guid Id,
    string Name,
    string Role,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset? LastUsedAtUtc,
    DateTimeOffset? RevokedAtUtc,
    string Status);

/// <summary>ExpiresIn is "1h", "24h", "7d" and so on (never is refused: a join token is meant to be short-lived); Uses null is unlimited until expiry.</summary>
public sealed record CreateJoinTokenRequest(string? ExpiresIn = null, int? Uses = null);

/// <summary>Token is shown here and never again.</summary>
public sealed record CreateJoinTokenResponse(Guid Id, DateTimeOffset ExpiresAtUtc, int? UsesRemaining, string Token);

public sealed record JoinTokenDto(
    Guid Id,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    int? UsesRemaining,
    DateTimeOffset? RevokedAtUtc,
    string Status);

/// <summary>The Status values on <see cref="ApiKeyDto"/> and <see cref="JoinTokenDto"/>, as the CLI prints them.</summary>
public static class CredentialStatuses
{
    public const string Live = "live";
    public const string Revoked = "revoked";
    public const string Expired = "expired";
    public const string UsedUp = "used up";
}

/// <summary>What <see cref="AgentDto.CredentialState"/> can be: an agent that has never enrolled, one that holds a live credential, one whose credential was revoked and has not re-enrolled.</summary>
public static class AgentCredentialStates
{
    public const string None = "none";
    public const string Live = "live";
    public const string Revoked = "revoked";
}
