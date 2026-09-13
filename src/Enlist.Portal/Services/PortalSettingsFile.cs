using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;

using Enlist.ControlPlane.Contracts;

namespace Enlist.Portal.Services;

/// <summary>
/// Writes the portal's own key into its <c>appsettings.json</c>, protected, with an ACL.
///
/// THE PORTAL DOES THIS, NOT THE INSTALLER, and that is a deliberate split. The bootstrapper is a
/// .NET Framework 4.7.2 process that runs before .NET 10 exists, so teaching it to merge JSON means
/// dragging System.Text.Json and half a dozen supporting assemblies into a payload Burn extracts
/// before anything is installed. This executable already has a JSON writer, already knows the shape
/// of its own configuration, and is already the thing that owns the DPAPI value. The installer runs
/// one command and reads an exit code.
///
/// MERGED, NEVER REWRITTEN. The published appsettings.json carries logging levels and AllowedHosts;
/// an install that replaced it would take the logging configuration with it and nobody would notice
/// until they needed a log.
/// </summary>
public static class PortalSettingsFile
{
    public const string FileName = "appsettings.json";

    /// <summary>
    /// The setting written, as the two nested names it actually is. PortalCredential reads it through
    /// <c>ControlPlane:ApiKey</c>; the colon is configuration's path separator, not part of the name,
    /// and writing a literal "ControlPlane:ApiKey" key into the JSON would produce a file the portal
    /// reads as nothing at all.
    /// </summary>
    public const string Section = "ControlPlane";
    public const string Key = "ApiKey";

    /// <summary>
    /// The file's new contents: <paramref name="existingJson"/> with the key set, everything else
    /// left alone, and two-space indentation because a human will read this file next.
    ///
    /// An empty or absent file becomes a new object. A file that is not valid JSON is NOT overwritten
    /// - it throws, because the alternative is discarding configuration somebody wrote by hand.
    /// </summary>
    public static string Merge(string? existingJson, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A value is required.", nameof(value));
        }

        JsonObject root;
        if (string.IsNullOrWhiteSpace(existingJson))
        {
            root = new JsonObject();
        }
        else
        {
            try
            {
                root = JsonNode.Parse(existingJson!, documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }) as JsonObject ?? throw new InvalidOperationException($"{FileName} does not contain a JSON object.");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"{FileName} is not valid JSON ({ex.Message}), so the portal's key cannot be stored in it without discarding what is there. Fix the file and run this again.");
            }
        }

        if (root[Section] is not JsonObject section)
        {
            section = new JsonObject();
            root[Section] = section;
        }

        section[Key] = value;

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    /// <summary>
    /// Reads, merges and writes the file beside this executable, then closes its ACL to SYSTEM,
    /// Administrators, this process, and the account the portal service will run as.
    ///
    /// The ACL is the actual control. Machine-scope DPAPI is decryptable by any process on this
    /// machine, so a protected value in a world-readable file is only protected from a casual glance,
    /// a backup or a pasted config - which is worth having, and is not the same as being safe.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string Store(string directory, string value, string? serviceAccount)
    {
        var path = Path.Combine(directory, FileName);
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;
        var merged = Merge(existing, value);

        // Written through a temporary file in the same directory and moved into place, so a failure
        // part-way through leaves the old configuration rather than half of the new one.
        var temporary = path + ".new";
        File.WriteAllText(temporary, merged);

        try
        {
            var info = new FileInfo(temporary);
            info.SetAccessControl(WindowsSecrets.ClosedTo(WindowsSecrets.Readers(serviceAccount)));

            // Overwrites atomically where the filesystem can, and keeps the ACL just applied.
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }

        return path;
    }
}
