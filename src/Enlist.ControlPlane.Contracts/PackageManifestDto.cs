namespace Enlist.ControlPlane.Contracts;

/// <summary>
/// The static "what's inside this package" catalog — extracted once at GET-time via a metadata-only
/// scan of the uploaded blob's [EnlistService]/[EnlistJob]-attributed types (see
/// Enlist.ControlPlane's PackageManifestScanner), independent of whether any agent has ever actually
/// run this package. Unlike AgentApplicationStatusDto's Services/Jobs, nothing here implies the
/// package is currently deployed anywhere — it's a fact about the blob itself.
/// </summary>
public sealed record PackageManifestDto(string? ApplicationDescription, IReadOnlyList<PackageManifestEntryDto> Entries);

public sealed record PackageManifestEntryDto(string Name, string Type, string? Description);
