namespace Enlist.ControlPlane.Contracts;

/// <summary>
/// The two caller-supplied strings that end up as filesystem paths, process names and container
/// names on every agent, validated in one place so the control plane, the portal, the CLI and the
/// agent apply the same rule — a value that is legal at one boundary must not be a path at another.
/// </summary>
public static class PackageDigests
{
    public const int Length = 64;

    /// <summary>Exactly 64 hexadecimal characters — a SHA-256 as PackageBlobStore computes it. Case-insensitive; store and compare the <see cref="Normalize"/>d form.</summary>
    public static bool IsValid(string? digest) => digest is { Length: Length } && digest.All(char.IsAsciiHexDigit);

    public static string Normalize(string digest) => digest.ToLowerInvariant();

    public static string Requirement => "a package digest is the 64-character hexadecimal SHA-256 of the uploaded bytes, exactly as POST /api/packages returned it";
}

public static class ApplicationNames
{
    public const int MaxLength = 64;

    // Windows treats these as devices in any directory ("NUL", "com1.txt"), and a name is a directory on
    // every agent. Matched on the stem before the first dot, which is how Windows itself matches them.
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Letters, digits, '-', '_' and '.', starting with a letter or digit, not ending with '.', at most
    /// 64 characters, and not a reserved device name. Room for "OrderProcessor" and "billing-api.v2";
    /// no room for a separator, a "..", a control character or anything a shell or a file system
    /// would read as something other than a name.
    /// </summary>
    public static bool IsValid(string? name) =>
        !string.IsNullOrEmpty(name) &&
        name.Length <= MaxLength &&
        char.IsAsciiLetterOrDigit(name[0]) &&
        name[^1] != '.' &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') &&
        !ReservedDeviceNames.Contains(name.Split('.')[0]);

    public static string Requirement => $"an application name is 1-{MaxLength} characters of letters, digits, '-', '_' and '.', starting with a letter or digit and not a reserved device name";
}
