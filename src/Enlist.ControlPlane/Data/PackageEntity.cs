using Enlist.ControlPlane.Contracts;

namespace Enlist.ControlPlane.Data;

/// <summary>
/// Metadata only — the actual bytes live on disk under PackageStorage, keyed by digest (see
/// Program.cs's package endpoints). Digest is the primary key and is always computed server-side from
/// the uploaded bytes, never trusted from the client, so it's an actual content hash, not a label a
/// caller could spoof.
/// </summary>
public sealed class PackageEntity
{
    public required string Digest { get; set; }

    public long SizeBytes { get; set; }

    public DateTimeOffset UploadedAtUtc { get; set; }

    /// <summary>
    /// Set from the uploader's ?application= query parameter (enlist-deploy always sends its --app
    /// name; a raw POST to /api/packages can omit it). Null if never provided. First-write-wins and
    /// otherwise immutable — a digest is a stable identity, so a later upload of the same bytes under a
    /// different name backfills a still-null value rather than silently relabeling a package someone
    /// might currently be identifying by this name (including one they're mid-incident trying to roll
    /// back to).
    /// </summary>
    public string? ApplicationName { get; set; }

    /// <summary>
    /// A per-ApplicationName counter (1, 2, 3, ...) assigned once at creation — set alongside
    /// ApplicationName by the same first-write-wins rule, so it shares that field's null-ness. Computed
    /// server-side as (highest existing VersionNumber for this name) + 1, NOT as "this row's position
    /// among packages that still exist" — the latter would silently renumber every remaining package
    /// whenever an older one gets garbage-collected by PackageRetentionSweepService, which is exactly
    /// the kind of surprise a human picking "v3" from a list must never see happen to what they already
    /// know as v3. A gap (v1 deleted, only v2/v3 remain) is fine; a renumbered v3 becoming v2 is not.
    /// </summary>
    public int? VersionNumber { get; set; }

    /// <summary>
    /// Null while at least one assignment currently points at this digest. Set to "now" the first
    /// sweep that finds zero assignments referencing it, and cleared again if it becomes referenced
    /// again later (e.g. rolled back to). This is the mark half of mark-and-sweep — PackageRetention
    /// SweepService only deletes a package once this has been non-null for longer than the configured
    /// retention period, which is what preserves the rollback window: a digest superseded five minutes
    /// ago is NOT immediately eligible just because it was originally uploaded months earlier.
    /// </summary>
    public DateTimeOffset? UnreferencedSinceUtc { get; set; }

    /// <summary>
    /// Detected server-side from the uploaded zip's own shape — presence of a *.deps.json entry means
    /// a net10.0 (SDK-style) build, its absence means net472 (see DetectRuntimeFlavor in Program.cs).
    /// Never trusted from the client and never asked of the caller — the CLI has no flag for it, so
    /// the bytes themselves are the only source. Re-detected on every upload of this digest, not just
    /// the first.
    /// </summary>
    public string RuntimeFlavor { get; set; } = RuntimeFlavors.Default;

    /// <summary>
    /// The scan result (PackageManifestDto, serialized) — what the portal shows as "what's in this
    /// package". Computed once at upload, like RuntimeFlavor and for the same reason: it is a fact about
    /// the bytes, and extracting the zip and reflecting over every DLL on every request was the cost the
    /// Applications page paid once per digest on every refresh. Null for a row from before this column
    /// existed, or when the scan failed at upload; the manifest endpoint scans such a row on first
    /// request and stores the result.
    /// </summary>
    public string? ManifestJson { get; set; }
}
