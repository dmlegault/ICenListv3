namespace Enlist.ControlPlane.Contracts;

/// <summary>
/// What GET /health answers. Status is "Healthy" (HTTP 200) or "Unhealthy" (HTTP 503), and it turns
/// on one thing only: whether the control plane's database answers. Product and Version are there so
/// a caller that reached *something* on a URL can tell it reached enList, and which build — the
/// installer's "verify this control plane URL" and the portal's reachability chip both want that, not
/// a bare 200. The body never carries the connection string or any credential.
/// </summary>
public sealed record HealthDto(
    string Status,
    string Product,
    string Version,
    HealthDatabaseDto Database,
    DateTimeOffset CheckedAtUtc);

/// <summary>LatestMigration is the newest applied EF migration id, so a probe (or the installer's Database page) can tell whether the schema is current without a second call. Error is set only when Reachable is false, and is a message, never a connection string.</summary>
public sealed record HealthDatabaseDto(bool Reachable, string? LatestMigration, string? Error);
