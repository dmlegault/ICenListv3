using Enlist.Portal.Services;

namespace Enlist.Portal.Tests;

/// <summary>
/// Key/value rows typed by a person, collapsed into a dictionary without the throw that used to take
/// the whole page down.
///
/// The policy wizard builds its selector inside a property that is read during RENDER. ToDictionary
/// throws ArgumentException on a duplicate key, so the instant someone typed a key a second time the
/// exception left the render loop, and nothing catches that - the dialogs only guard against
/// ApiException. The circuit died: blank page, every field in the dialog lost, and the sole trace a
/// duplicate-key line in the server log. The person had done nothing but type.
/// </summary>
public sealed class KeyedRowsTests
{
    [Fact]
    public void A_duplicate_key_collapses_instead_of_throwing()
    {
        var collapsed = KeyedRows.Collapse([("env", "demo"), ("env", "prod")]);

        // Last wins, and - the point - it returns at all.
        Assert.Equal("prod", collapsed["env"]);
        Assert.Single(collapsed);
    }

    [Fact]
    public void Blank_keys_are_dropped_and_keys_are_trimmed()
    {
        // An empty row is a row someone is about to fill in, not an error, and " env" is a typo that
        // would otherwise become a second, invisible tag.
        var collapsed = KeyedRows.Collapse([("", "ignored"), ("   ", "ignored"), (" env ", "demo"), (null, "ignored")]);

        Assert.Equal(new[] { "env" }, collapsed.Keys);
        Assert.Equal("demo", collapsed["env"]);
    }

    [Fact]
    public void A_missing_value_is_an_empty_string_not_a_null()
    {
        Assert.Equal("", KeyedRows.Collapse([("env", null)])["env"]);
    }

    [Fact]
    public void Duplicates_are_reported_by_name_so_the_message_can_say_which()
    {
        var message = KeyedRows.DuplicateMessage(["env", "env", "tier", "tier", "role"], "tag key");

        Assert.NotNull(message);
        Assert.Contains("'env'", message);
        Assert.Contains("'tier'", message);
        Assert.DoesNotContain("'role'", message);
    }

    [Fact]
    public void No_duplicates_means_no_message_at_all()
    {
        // Null rather than an empty string, so a caller reads as "if there is something to say, say
        // it and stop" rather than having to test for emptiness.
        Assert.Null(KeyedRows.DuplicateMessage(["env", "tier"], "tag key"));
        Assert.Null(KeyedRows.DuplicateMessage(["env", "", "  "], "tag key"));
    }

    [Fact]
    public void Duplicate_detection_ignores_surrounding_space_the_same_way_collapsing_does()
    {
        // Or the two would disagree: the save path would report no duplicates while the collapse
        // silently dropped one of the rows.
        Assert.NotNull(KeyedRows.DuplicateMessage(["env", " env "], "tag key"));
    }
}
