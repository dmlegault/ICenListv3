using System.Net;

using Enlist.TestSupport;

namespace Enlist.Portal.Tests;

/// <summary>
/// The portal's side of authentication (Authentication-Design.md sections 5 and 8), against the real
/// portal as a process and a real control plane running Required: the Negotiate challenge, Windows
/// sign-in as the account running the tests, the two group-to-role policies, the page a person in
/// neither group gets, and the same two listener rules the control plane has. The account running the
/// tests is a member of "Authenticated Users" and not of "Guests", which is all the group arithmetic
/// these tests need.
///
/// The three tests that actually sign in skip themselves - with the reason - on a machine whose
/// account cannot authenticate to it over the network: a workgroup machine refuses NTLM to itself
/// (LSA's loopback check), and an account signed in without a network-usable secret (a Microsoft
/// account, Windows Hello) cannot even start the handshake. Both are properties of the machine, not
/// of the portal, and both show up as one of two signatures the helper below recognizes. What they
/// would prove - that a Windows identity is turned into the two roles and that the pages honour them
/// - is pinned without a handshake by PortalRolesTests.
/// </summary>
public sealed class PortalAuthenticationTests : IAsyncLifetime
{
    /// <summary>
    /// Set the first time a Windows sign-in actually succeeds in this process. From then on a 400
    /// cannot be this machine refusing itself, so it is a failure rather than a skip - see
    /// SignedInAsync.
    /// </summary>
    private static bool _signInHasWorkedHere;

    private const string Everyone = @"NT AUTHORITY\Authenticated Users";
    private const string Nobody = @"BUILTIN\Guests";

    private static readonly IReadOnlyDictionary<string, string> Required = AuthenticationMode.Required;

    private ControlPlaneTestServer? _controlPlane;
    private string _portalKey = "";

    public async Task InitializeAsync()
    {
        _controlPlane = await ControlPlaneTestServer.StartAsync(TimeSpan.FromSeconds(30), Required);
        _portalKey = await _controlPlane.CreateApiKeyAsync("portal", "Operator", "--expires", "never");
    }

    public async Task DisposeAsync()
    {
        if (_controlPlane is not null)
        {
            await _controlPlane.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task Required_challenges_an_anonymous_browser_and_admits_a_windows_user_in_the_operators_group()
    {
        await using var portal = await StartPortalAsync("Required", operators: Everyone, viewers: null);

        // The challenge needs no sign-in and is asserted before the skip can happen.
        using var anonymous = PortalTestServer.CreateClient(portal.BaseUri, asWindowsUser: false);
        using var challenge = await anonymous.GetAsync("/");
        Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);
        Assert.Contains(challenge.Headers.WwwAuthenticate, h => h.Scheme == "Negotiate");

        // The prerendered page is what a browser gets first: the app bar says who you are, the
        // Agents page offers the Operator-only control, and the Operator-only page renders.
        using var me = AsWindowsUser(portal.BaseUri);
        var home = await SignedInAsync(me, "/");
        Assert.Contains(Environment.UserName, home, StringComparison.OrdinalIgnoreCase);

        var agents = await SignedInAsync(me, "/agents");
        Assert.Contains("Enroll agent", agents);

        var access = await SignedInAsync(me, "/access");
        Assert.Contains("Join tokens", access);
        Assert.Contains("API keys", access);
    }

    [SkippableFact]
    public async Task A_viewer_sees_every_page_and_no_operator_controls()
    {
        await using var portal = await StartPortalAsync("Required", operators: Nobody, viewers: Everyone);

        using var me = AsWindowsUser(portal.BaseUri);
        var home = await SignedInAsync(me, "/");
        Assert.Contains(Environment.UserName, home, StringComparison.OrdinalIgnoreCase);   // admitted
        Assert.DoesNotContain("Upload a package", home);

        var agents = await SignedInAsync(me, "/agents");
        Assert.Contains("No agents have registered yet", agents);   // the page renders
        Assert.DoesNotContain("Enroll agent", agents);              // without the Operator control

        // The Operator-only page is refused with a reason, not a blank.
        var access = await SignedInAsync(me, "/access");
        Assert.Contains("Operator", access);
        Assert.DoesNotContain("New API key", access);
    }

    [SkippableFact]
    public async Task A_windows_user_in_neither_group_gets_a_page_naming_the_groups()
    {
        await using var portal = await StartPortalAsync("Required", operators: Nobody, viewers: Nobody);

        using var me = AsWindowsUser(portal.BaseUri);
        var page = await SignedInAsync(me, "/");
        Assert.Contains("not admitted", page);
        Assert.Contains(Nobody, page);
        Assert.DoesNotContain("No applications yet", page);   // the page itself was not rendered
    }

    /// <summary>A page fetched as the signed-in Windows user - or a skip, with the reason, when this machine cannot sign in to itself (see the class summary).</summary>
    private static async Task<string> SignedInAsync(HttpClient me, string path)
    {
        HttpResponseMessage response;
        try
        {
            response = await me.GetAsync(path);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("Authentication validation failed", StringComparison.OrdinalIgnoreCase))
        {
            throw new SkipException($"This account cannot start a Windows sign-in over the network on this machine ({ex.Message.Trim()}); a domain member, or an account with a network-usable password, can run this test.");
        }

        using (response)
        {
            // A 400 here is the LSA loopback refusal - UNLESS a sign-in has already succeeded in this
            // process, in which case the machine has demonstrably proved it can do this and a 400 is a
            // real regression that must fail rather than skip. Skipping on ANY 400 (as this did until
            // 2026-09-13) would have swallowed a genuine Negotiate break, which is precisely the kind
            // of test that is worse than none: it reports green on the machine where it matters most.
            Skip.If(response.StatusCode == HttpStatusCode.BadRequest && !_signInHasWorkedHere,
                "Windows refused this machine's own NTLM sign-in (LSA's loopback check); a domain member, or DisableLoopbackCheck, can run this test.");
            response.EnsureSuccessStatusCode();
            _signInHasWorkedHere = true;
            return await response.Content.ReadAsStringAsync();
        }
    }

    [Fact]
    public async Task Off_is_refused_off_loopback()
    {
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => StartPortalAsync("Off", operators: null, viewers: null, listenUrls: "http://0.0.0.0:0"));
        Assert.Contains("loopback", refused.Message);
        Assert.Contains("portal", refused.Message);
    }

    [Fact]
    public async Task Off_on_loopback_is_anonymous_and_everyone_is_an_operator()
    {
        // Off is permitted on loopback only, so this one listens there, in the clear.
        await using var portal = await StartPortalAsync("Off", operators: null, viewers: null, listenUrls: "http://127.0.0.1:0");

        using var anonymous = PortalTestServer.CreateClient(portal.BaseUri, asWindowsUser: false);
        var agents = await anonymous.GetStringAsync("/agents");
        Assert.Contains("Enroll agent", agents);
    }

    private Task<PortalTestServer> StartPortalAsync(string mode, string? operators, string? viewers, string listenUrls = "https://0.0.0.0:0") =>
        PortalTestServer.StartAsync(TimeSpan.FromSeconds(60), new Dictionary<string, string>
        {
            ["ControlPlane__BaseUrl"] = _controlPlane!.BaseUri.ToString(),
            ["ControlPlane__ApiKey"] = _portalKey,
            ["Authentication__Mode"] = mode,
            ["Authentication__Windows__OperatorsGroup"] = operators ?? "",
            ["Authentication__Windows__ViewersGroup"] = viewers ?? "",
        }, listenUrls);

    /// <summary>What an intranet browser does: sends the current Windows identity when challenged. Dials the machine by name, over HTTPS - see PortalTestServer for why neither is optional.</summary>
    private static HttpClient AsWindowsUser(Uri baseUri) => PortalTestServer.CreateClient(baseUri, asWindowsUser: true);
}
