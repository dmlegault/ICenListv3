using Enlist.ControlPlane.Contracts;
using Enlist.Portal.Services;

namespace Enlist.Portal.Tests;

/// <summary>
/// The portal's agreement rule, tested directly. It predicts what the control plane's
/// ResolveEffectivePoliciesForAgentAsync will decide, so every field resolution compares has to be
/// compared here too — Isolation is the one a private copy of this rule once missed.
/// </summary>
public sealed class PolicyConflictDetectorTests
{
    [Fact]
    public void Rules_that_say_the_same_thing_agree()
    {
        Assert.True(PolicyConflictDetector.Agree([Rule(), Rule()]));
    }

    [Fact]
    public void Rules_that_differ_only_in_isolation_mode_disagree()
    {
        var process = Rule(isolation: new IsolationSpec(IsolationModes.Process));
        var container = Rule(isolation: new IsolationSpec(IsolationModes.Container));

        Assert.False(PolicyConflictDetector.Agree([process, container]));
    }

    [Fact]
    public void A_rule_with_no_isolation_block_agrees_with_an_explicit_process_rule()
    {
        Assert.True(PolicyConflictDetector.Agree([Rule(isolation: null), Rule(isolation: IsolationSpec.ProcessDefault)]));
    }

    [Fact]
    public void Equal_port_lists_in_different_list_instances_agree()
    {
        var a = Rule(isolation: Container(new PortMapping(8080, 9000, "tcp", "http")));
        var b = Rule(isolation: Container(new PortMapping(8080, 9000, "tcp", "http")));

        Assert.True(PolicyConflictDetector.Agree([a, b]));
    }

    [Fact]
    public void Rules_that_publish_different_host_ports_disagree()
    {
        var a = Rule(isolation: Container(new PortMapping(8080, 9000)));
        var b = Rule(isolation: Container(new PortMapping(8080, 9001)));

        Assert.False(PolicyConflictDetector.Agree([a, b]));
    }

    [Fact]
    public void Rules_that_differ_in_a_cron_override_disagree()
    {
        var a = Rule(cron: Tags(("Nightly", "0 0 2 * * *")));
        var b = Rule(cron: Tags(("Nightly", "0 0 3 * * *")));

        Assert.False(PolicyConflictDetector.Agree([a, b]));
    }

    [Fact]
    public void FindConflictsByAgent_names_only_the_agents_matched_by_disagreeing_rules()
    {
        var everyDemoAgent = Rule(selector: Tags(("env", "demo")), isolation: IsolationSpec.ProcessDefault);
        var canaryInAContainer = Rule(selector: Tags(("canary", "yes")), isolation: new IsolationSpec(IsolationModes.Container));
        var plain = Agent("PLAIN", ("env", "demo"));
        var canary = Agent("CANARY", ("env", "demo"), ("canary", "yes"));

        var conflicts = PolicyConflictDetector.FindConflictsByAgent([plain, canary], [everyDemoAgent, canaryInAContainer]);

        var (agentName, rules) = Assert.Single(conflicts);
        Assert.Equal("CANARY", agentName);
        Assert.Equal(2, rules.Count);
    }

    [Fact]
    public void A_static_host_port_another_application_holds_on_the_same_agent_is_a_collision()
    {
        var agent = Agent("A", ("env", "demo"));
        var other = Rule(application: "Other", selector: Tags(("env", "demo")), isolation: Container(new PortMapping(80, 9000)));

        var collisions = PolicyConflictDetector.FindPortCollisions([agent], ["A"], "Mine", Container(new PortMapping(8080, 9000)), [other]);

        Assert.Equal(new PortCollision("A", 9000, "tcp", "Other"), Assert.Single(collisions));
    }

    [Fact]
    public void A_dynamic_host_port_never_collides()
    {
        var agent = Agent("A", ("env", "demo"));
        var other = Rule(application: "Other", selector: Tags(("env", "demo")), isolation: Container(new PortMapping(80, 9000)));

        Assert.Empty(PolicyConflictDetector.FindPortCollisions([agent], ["A"], "Mine", Container(new PortMapping(8080)), [other]));
    }

    [Fact]
    public void An_application_never_collides_with_its_own_rules()
    {
        var agent = Agent("A", ("env", "demo"));
        var existing = Rule(application: "Mine", selector: Tags(("env", "demo")), isolation: Container(new PortMapping(80, 9000)));

        Assert.Empty(PolicyConflictDetector.FindPortCollisions([agent], ["A"], "Mine", Container(new PortMapping(80, 9000)), [existing]));
    }

    private static readonly DateTimeOffset When = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);

    private static ApplicationPolicyDto Rule(
        string application = "OrderProcessor",
        IReadOnlyDictionary<string, string>? cron = null,
        IReadOnlyDictionary<string, string>? selector = null,
        IsolationSpec? isolation = null) =>
        new(Guid.NewGuid(), application, Path: null, "Running", cron ?? Tags(), When, selector ?? Tags(),
            PackageDigest: "0123456789abcdef", Isolation: isolation);

    private static IsolationSpec Container(params PortMapping[] ports) =>
        new(IsolationModes.Container, Ports: ports);

    private static AgentDto Agent(string name, params (string Key, string Value)[] tags) =>
        new(name, Tags(tags), When, When);

    private static Dictionary<string, string> Tags(params (string Key, string Value)[] tags) =>
        tags.ToDictionary(t => t.Key, t => t.Value);
}
