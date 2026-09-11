namespace Enlist.ControlPlane.Data;

/// <summary>
/// The one credential an agent holds, keyed by its name so a name can hold at most one — which is
/// what turns "two agents sharing a name" from silent report corruption into a 409 at enrollment
/// (Authentication-Design.md section 4.4). Only the SHA-256 of the token is stored; a database read
/// yields nothing a caller could present.
/// </summary>
public sealed class AgentCredentialEntity
{
    public required string AgentName { get; set; }

    public required string TokenHash { get; set; }

    public DateTimeOffset IssuedAtUtc { get; set; }

    /// <summary>Updated at most once a minute per credential, to keep the write off the hot path.</summary>
    public DateTimeOffset? LastUsedAtUtc { get; set; }

    public DateTimeOffset? RevokedAtUtc { get; set; }
}
