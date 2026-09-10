namespace Enlist.Portal.Services;

/// <summary>One service or job an application's package defines, as reported by the static package
/// manifest scan (see Enlist.ControlPlane's PackageManifestScanner) — the "what's inside this build"
/// catalog shown by ApplicationContentsDialog, independent of which agents currently run it or what
/// state it's in there.</summary>
public sealed record InstanceInfo(string Name, string Type, string? Description);

/// <summary>One distinct package build an application's policies currently reference — kept separate
/// per version rather than flattened into one merged list, since two versions can legitimately define
/// different services/jobs (or the same name with a changed description) and silently merging them
/// would hide exactly that difference.</summary>
public sealed record VersionManifest(int? VersionNumber, string Digest, List<InstanceInfo> Instances);
