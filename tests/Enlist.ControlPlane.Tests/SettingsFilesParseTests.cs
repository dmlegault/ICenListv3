using System.Text.Json;

using Enlist.TestSupport;

namespace Enlist.ControlPlane.Tests;

/// <summary>
/// Every appsettings*.json in src\ is valid JSON, judged by the same rules the host reads it with.
///
/// Not a formality. On 2026-09-12 the control plane's LocalDB connection string moved from
/// appsettings.json into appsettings.Development.json, and one backslash of "(localdb)\\mssqllocaldb"
/// was lost on the way. "\m" is not a JSON escape, so from then on every Development run of the
/// control plane - `dotnet run` through its launch profile, and `dotnet ef` by default - failed at
/// startup with "Failed to load configuration from file". It was found two days later, by accident,
/// while checking something else.
///
/// Nothing in the suite could see it. ControlPlaneTestServer does run the control plane as
/// Development, but from the test's own directory, where that file does not exist - and a missing
/// settings file is optional, so the host carries on without it. A test that reads the files where
/// they are is the only thing that reaches them.
/// </summary>
public sealed class SettingsFilesParseTests
{
    // What Microsoft.Extensions.Configuration.Json accepts: comments and trailing commas are allowed,
    // anything else System.Text.Json rejects is rejected at startup too.
    private static readonly JsonDocumentOptions HostRules = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    [Fact]
    public void Every_settings_file_under_src_parses_as_the_host_would_read_it()
    {
        var src = Path.Combine(RepoPaths.RepositoryRoot(), "src");
        var files = Directory.EnumerateFiles(src, "appsettings*.json", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();

        // An empty scan would pass for the wrong reason - a moved folder, a changed pattern.
        Assert.Contains(files, f => f.EndsWith(Path.Combine("Enlist.ControlPlane", "appsettings.Development.json")));

        var broken = new List<string>();
        foreach (var file in files)
        {
            try
            {
                using var _ = JsonDocument.Parse(File.ReadAllText(file), HostRules);
            }
            catch (JsonException ex)
            {
                broken.Add($"{Path.GetRelativePath(src, file)}: {ex.Message}");
            }
        }

        Assert.True(broken.Count == 0,
            "A settings file the host cannot read fails every run that loads it, at startup. " +
            $"In a JSON string a backslash is written \\\\.{Environment.NewLine}{string.Join(Environment.NewLine, broken)}");
    }
}
