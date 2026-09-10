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

    private static readonly DateTimeOffset When = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);

    private static AgentDto Agent(string name, params (string Key, string Value)[] tags) =>
        new(name, Tags(tags), When, When);

    private static Dictionary<string, string> Tags(params (string Key, string Value)[] tags) =>
        tags.ToDictionary(t => t.Key, t => t.Value);
}
