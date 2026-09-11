namespace Enlist.ControlPlane.Data;

/// <summary>
/// What an operator hands to a machine they have not met yet. Short-lived, optionally multi-use, and
/// good for exactly one thing: POST /api/agents/enroll, which exchanges it for an agent credential.
/// See Authentication-Design.md section 4.
/// </summary>
public sealed class JoinTokenEntity
{
    public Guid Id { get; set; }

    public required string TokenHash { get; set; }

    public required string CreatedBy { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset ExpiresAtUtc { get; set; }

    /// <summary>Null means unlimited uses until expiry; otherwise decremented on each enrollment and refused at zero.</summary>
    public int? UsesRemaining { get; set; }

    public DateTimeOffset? RevokedAtUtc { get; set; }
}
