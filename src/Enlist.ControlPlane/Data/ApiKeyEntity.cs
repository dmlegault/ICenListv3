using Enlist.ControlPlane.Contracts;

namespace Enlist.ControlPlane.Data;

/// <summary>
/// A management credential: what the portal, enlist-deploy and a CI pipeline authenticate with, at
/// the role it carries (<see cref="ManagementRoles"/>). Named so it can be revoked by name and shows
/// up as itself in the audit log. See Authentication-Design.md section 6.
/// </summary>
public sealed class ApiKeyEntity
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public required string KeyHash { get; set; }

    public required string Role { get; set; }

    public required string CreatedBy { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>Null means the key never expires — the portal's own key is created that way.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; set; }

    public DateTimeOffset? LastUsedAtUtc { get; set; }

    public DateTimeOffset? RevokedAtUtc { get; set; }
}
