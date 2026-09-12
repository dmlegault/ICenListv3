using Enlist.ControlPlane.Contracts;

using Microsoft.Extensions.Configuration;

namespace Enlist.ControlPlane.Tests;

/// <summary>
/// ListenerRules as a pure function, which is what it was built to be: no server, no process, just
/// the question "given this configuration, may authentication be Off here?". The end-to-end proof
/// that a real control plane refuses to start lives in AuthenticationTests; these pin the part that
/// decides it, including the source that was missing until 2026-09-12.
///
/// ConfiguredUrls has to see EVERY source a listen address can come from. It saw two of them -
/// --urls and Kestrel:Endpoints - and not ASPNETCORE_HTTP_PORTS, which arrives as the HTTP_PORTS
/// key and which the official ASP.NET Core container images set to 8080 on their own. A
/// containerised control plane with Mode=Off therefore passed the check while binding every
/// interface: an unauthenticated control plane reachable from the network, the single state the
/// rule exists to make unconfigurable.
/// </summary>
public sealed class ListenerRulesTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public void A_bare_port_list_is_read_as_every_interface_and_refuses_Off()
    {
        // The container case, exactly as the base image leaves it.
        var urls = ListenerRules.ConfiguredUrls(Configuration(("HTTP_PORTS", "8080")));

        Assert.Equal(new[] { "http://*:8080" }, urls);

        var violation = ListenerRules.Violation(urls, AuthenticationModes.Off, "control plane");
        Assert.NotNull(violation);
        Assert.Contains("off loopback", violation);
        Assert.Contains("no override", violation);
    }

    [Fact]
    public void Https_ports_are_read_too_and_are_refused_under_Off_but_allowed_under_Required()
    {
        var urls = ListenerRules.ConfiguredUrls(Configuration(("HTTPS_PORTS", "8443")));

        Assert.Equal(new[] { "https://*:8443" }, urls);

        // Off is about reachability, so TLS does not rescue it...
        Assert.NotNull(ListenerRules.Violation(urls, AuthenticationModes.Off, "control plane"));

        // ...while Required is about credentials in the clear, which https already answers.
        Assert.Null(ListenerRules.Violation(urls, AuthenticationModes.Required, "control plane"));
    }

    [Fact]
    public void Semicolon_separated_ports_each_become_their_own_listener()
    {
        var urls = ListenerRules.ConfiguredUrls(Configuration(("HTTP_PORTS", "8080;8081"), ("HTTPS_PORTS", "8443")));

        Assert.Equal(new[] { "http://*:8080", "http://*:8081", "https://*:8443" }, urls);
    }

    [Fact]
    public void Every_source_is_collected_not_just_the_first_one_that_answers()
    {
        // A union, deliberately, rather than a model of Kestrel's precedence: where the sources
        // disagree this refuses a configuration that might in fact have bound only loopback, which
        // costs a startup and a clear message. Guessing precedence wrongly the other way costs an
        // unauthenticated control plane on a network.
        var urls = ListenerRules.ConfiguredUrls(Configuration(
            ("urls", "http://localhost:5293"),
            ("Kestrel:Endpoints:Https:Url", "https://localhost:7216"),
            ("HTTP_PORTS", "8080")));

        Assert.Equal(new[] { "http://localhost:5293", "https://localhost:7216", "http://*:8080" }, urls);
        Assert.NotNull(ListenerRules.Violation(urls, AuthenticationModes.Off, "control plane"));
    }

    [Fact]
    public void Loopback_from_every_source_together_still_permits_Off()
    {
        // The developer setup and the demo: --urls on loopback, nothing else configured. This is the
        // case that must not regress while the rule is tightened.
        var urls = ListenerRules.ConfiguredUrls(Configuration(("urls", "http://localhost:5293;http://127.0.0.1:5294")));

        Assert.Null(ListenerRules.Violation(urls, AuthenticationModes.Off, "control plane"));
    }

    [Fact]
    public void Nothing_configured_at_all_is_Kestrels_own_loopback_default()
    {
        var urls = ListenerRules.ConfiguredUrls(Configuration());

        Assert.Empty(urls);
        Assert.Null(ListenerRules.Violation(urls, AuthenticationModes.Off, "control plane"));
    }

    [Fact]
    public void An_empty_or_whitespace_port_setting_contributes_no_listener()
    {
        Assert.Empty(ListenerRules.ConfiguredUrls(Configuration(("HTTP_PORTS", ""), ("HTTPS_PORTS", "   "))));
    }
}
