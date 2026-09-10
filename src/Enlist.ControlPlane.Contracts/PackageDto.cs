namespace Enlist.ControlPlane.Contracts;

public sealed record PackageInfo(string Digest, long SizeBytes, DateTimeOffset UploadedAtUtc, string? ApplicationName = null, int? VersionNumber = null, string RuntimeFlavor = RuntimeFlavors.Default);

public sealed record UploadPackageResponse(string Digest, long SizeBytes, bool AlreadyExisted, string RuntimeFlavor = RuntimeFlavors.Default);
