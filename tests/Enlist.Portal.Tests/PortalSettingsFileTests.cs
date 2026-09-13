using System.Text.Json;
using System.Text.Json.Nodes;

using Enlist.Portal.Services;

using Microsoft.Extensions.Configuration;

namespace Enlist.Portal.Tests;

/// <summary>
/// Writing the portal's key into its own appsettings.json - what
/// <c>Enlist.Portal.exe protect &lt;key&gt; --store</c> does for the installer.
///
/// The merge is the part worth testing. A published appsettings.json already carries logging levels
/// and AllowedHosts, and an install that rewrote the file rather than merging would take them with
/// it - silently, and only noticed the first time somebody needed a log.
/// </summary>
public sealed class PortalSettingsFileTests
{
    private const string Published = """
        {
          "Logging": {
            "LogLevel": {
              "Default": "Information",
              "Microsoft.AspNetCore": "Warning"
            }
          },
          "AllowedHosts": "*"
        }
        """;

    private static string? ApiKeyIn(string json) =>
        JsonNode.Parse(json)?[PortalSettingsFile.Section]?[PortalSettingsFile.Key]?.GetValue<string>();

    [Fact]
    public void The_key_is_added_and_everything_else_survives()
    {
        var merged = PortalSettingsFile.Merge(Published, "dpapi:AQAAAA==");

        Assert.Equal("dpapi:AQAAAA==", ApiKeyIn(merged));

        var root = JsonNode.Parse(merged)!;
        Assert.Equal("*", root["AllowedHosts"]!.GetValue<string>());
        Assert.Equal("Warning", root["Logging"]!["LogLevel"]!["Microsoft.AspNetCore"]!.GetValue<string>());
    }

    [Fact]
    public void The_setting_is_written_as_nested_names_not_as_a_literal_colon()
    {
        var merged = PortalSettingsFile.Merge(Published, "dpapi:x");

        // PortalCredential reads ControlPlane:ApiKey, where the colon is configuration's path
        // separator. A literal "ControlPlane:ApiKey" property would be a key the portal never finds,
        // and the failure would be a 401 at runtime rather than anything pointing here.
        Assert.DoesNotContain("\"ControlPlane:ApiKey\"", merged, StringComparison.Ordinal);
        Assert.NotNull(JsonNode.Parse(merged)![PortalSettingsFile.Section]);
    }

    [Fact]
    public void An_existing_key_is_replaced_rather_than_doubled()
    {
        var once = PortalSettingsFile.Merge(Published, "dpapi:first");
        var twice = PortalSettingsFile.Merge(once, "dpapi:second");

        // A repair or an upgrade mints a new key and stores it over the old one.
        Assert.Equal("dpapi:second", ApiKeyIn(twice));
        Assert.Single(JsonNode.Parse(twice)![PortalSettingsFile.Section]!.AsObject());
    }

    [Fact]
    public void Other_settings_already_under_ControlPlane_are_kept()
    {
        var withBaseUrl = """{"ControlPlane":{"BaseUrl":"https://cp.example:5293"}}""";

        var merged = PortalSettingsFile.Merge(withBaseUrl, "dpapi:x");

        // BaseUrl is required for the portal to start at all, so losing it while storing the key
        // would turn a working install into one that throws on startup.
        Assert.Equal("https://cp.example:5293", JsonNode.Parse(merged)!["ControlPlane"]!["BaseUrl"]!.GetValue<string>());
        Assert.Equal("dpapi:x", ApiKeyIn(merged));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_absent_or_empty_file_becomes_a_new_object(string? existing)
    {
        var merged = PortalSettingsFile.Merge(existing, "dpapi:x");
        Assert.Equal("dpapi:x", ApiKeyIn(merged));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string\"")]
    public void A_file_that_is_not_a_json_object_is_refused_rather_than_overwritten(string existing)
    {
        // Discarding hand-written configuration to store a key is worse than failing the step: the
        // installer reports it, and what was there is still there.
        Assert.Throws<InvalidOperationException>(() => PortalSettingsFile.Merge(existing, "dpapi:x"));
    }

    [Fact]
    public void Comments_and_trailing_commas_are_tolerated_the_way_configuration_tolerates_them()
    {
        var handWritten = """
            {
              // the portal talks to this one
              "ControlPlane": { "BaseUrl": "https://cp.example:5293", },
            }
            """;

        var merged = PortalSettingsFile.Merge(handWritten, "dpapi:x");

        Assert.Equal("https://cp.example:5293", JsonNode.Parse(merged)!["ControlPlane"]!["BaseUrl"]!.GetValue<string>());
        Assert.Equal("dpapi:x", ApiKeyIn(merged));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_empty_value_is_a_programming_error(string? value)
    {
        Assert.Throws<ArgumentException>(() => PortalSettingsFile.Merge(Published, value!));
    }

    [Fact]
    public void The_result_is_indented_and_newline_terminated_because_a_person_reads_it_next()
    {
        var merged = PortalSettingsFile.Merge(Published, "dpapi:x");

        Assert.Contains("\n  ", merged, StringComparison.Ordinal);
        Assert.EndsWith(Environment.NewLine, merged, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stored_value_round_trips_through_the_configuration_the_portal_actually_reads()
    {
        var merged = PortalSettingsFile.Merge(Published, ProtectedSettings.Protect("enlk_round_trip"));
        var path = Path.Combine(Path.GetTempPath(), "enlist-portal-settings-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, merged);

        try
        {
            // The end-to-end claim: what --store writes is what PortalCredential reads back. Asserting
            // the JSON shape alone would pass just as happily with the setting under the wrong name.
            var configuration = new ConfigurationBuilder().AddJsonFile(path).Build();

            Assert.Equal("enlk_round_trip", PortalCredential.FromConfiguration(configuration).Key);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
