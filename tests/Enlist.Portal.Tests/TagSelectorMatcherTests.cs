using Enlist.ControlPlane.Contracts;
using Enlist.Portal.Services;

namespace Enlist.Portal.Tests;

/// <summary>The portal's copy of the control plane's selector matching — including the implicit "agent" self-tag that AgentDto.Tags never carries.</summary>
public sealed class TagSelectorMatcherTests
{
    [Fact]
    public void A_selector_naming_an_agent_matches_it_through_the_implicit_self_tag()
    {
        var selector = Tags(("agent", "DEV-01"));

        Assert.True(TagSelectorMatcher.Matches(Agent("DEV-01"), selector));
        Assert.False(TagSelectorMatcher.Matches(Agent("DEV-02"), selector));
        Assert.Equal(new[] { "DEV-01" }, TagSelectorMatcher.ResolveAgentNames([Agent("DEV-01"), Agent("DEV-02")], selector));
    }

    [Fact]
    public void An_empty_selector_matches_every_agent()
    {
        var agents = new[] { Agent("A"), Agent("B", ("env", "demo")) };

        Assert.Equal(new[] { "A", "B" }, TagSelectorMatcher.ResolveAgentNames(agents, Tags()));
    }

    [Fact]
    public void Every_pair_in_the_selector_has_to_match()
    {
        var agent = Agent("A", ("env", "demo"));

        Assert.True(TagSelectorMatcher.Matches(agent, Tags(("env", "demo"))));
        Assert.False(TagSelectorMatcher.Matches(agent, Tags(("env", "demo"), ("canary", "yes"))));
        Assert.False(TagSelectorMatcher.Matches(agent, Tags(("env", "prod"))));
    }

    [Fact]
    public void Only_the_bare_self_tag_counts_as_targeting_one_agent()
    {
        var agent = Agent("A", ("env", "demo"));

        Assert.True(TagSelectorMatcher.IsSelfTargeted(agent, Tags(("agent", "A"))));
        Assert.False(TagSelectorMatcher.IsSelfTargeted(agent, Tags(("agent", "A"), ("env", "demo"))));
        Assert.False(TagSelectorMatcher.IsSelfTargeted(Agent("B"), Tags(("agent", "A"))));
    }

    [Fact]
    public void DescribeScope_uses_the_same_three_phrasings_every_screen_shows()
    {
        var agent = Agent("A", ("env", "demo"));

        Assert.Equal("this agent only", TagSelectorMatcher.DescribeScope(agent, Tags(("agent", "A"))));
        Assert.Equal("every agent", TagSelectorMatcher.DescribeScope(agent, Tags()));
        Assert.Equal("tag: env=demo", TagSelectorMatcher.DescribeScope(agent, Tags(("env", "demo"))));
    }

    [Fact]
    public void The_self_tag_matches_an_agent_name_in_any_case()
    {
        // Agent names are case-insensitive everywhere else in the system: the SQL collation, the
        // bearer handler's route check, the hub's group check. Matching them exactly here meant a
        // rule targeting "web-07" silently matched nothing on agent "WEB-07" - no error, no warning,
        // no log line, just an application that never ran and a rule that looked right on screen.
        var agent = Agent("WEB-07");

        Assert.True(TagSelectorMatcher.Matches(agent, Tags(("agent", "web-07"))));
        Assert.True(TagSelectorMatcher.Matches(agent, Tags(("agent", "WeB-07"))));
        Assert.True(TagSelectorMatcher.IsSelfTargeted(agent, Tags(("agent", "web-07"))));

        // A DIFFERENT name is still a different agent; case-insensitive is not lenient.
        Assert.False(TagSelectorMatcher.Matches(agent, Tags(("agent", "web-08"))));
    }

    [Fact]
    public void An_ordinary_tag_is_still_matched_exactly()
    {
        // Deliberately not extended to ordinary tags. Their keys and values are operator-chosen text,
        // and deciding that "env=Prod" and "env=prod" mean the same thing is a policy judgement
        // nobody has made. The self-tag is the exception because its value is an agent name.
        var agent = Agent("A", ("env", "prod"));

        Assert.True(TagSelectorMatcher.Matches(agent, Tags(("env", "prod"))));
        Assert.False(TagSelectorMatcher.Matches(agent, Tags(("env", "Prod"))));
    }

    private static readonly DateTimeOffset When = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);

    private static AgentDto Agent(string name, params (string Key, string Value)[] tags) =>
        new(name, Tags(tags), When, When);

    private static Dictionary<string, string> Tags(params (string Key, string Value)[] tags) =>
        tags.ToDictionary(t => t.Key, t => t.Value);
}
