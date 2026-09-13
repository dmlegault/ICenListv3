using System.Net.Http.Headers;
using System.Security.AccessControl;
using System.Security.Principal;

using Enlist.Agent.Credentials;
using Enlist.ControlPlane.Contracts;
using Enlist.TestSupport;

namespace Enlist.Agent.Tests;

/// <summary>
/// <c>enlist-agent enroll</c>, against a real control plane running Required - the verb the installer
/// uses to exchange a join token before the agent service is ever started.
///
/// What makes it worth its own file rather than a case in AgentCredentialTests: the thing being
/// asserted is that it ENDS. A normal start with --join-token enrolls and then goes on to connect the
/// hub, read assignments and reconcile the machine. This has to store a file and exit, because the
/// installer runs it between creating a stopped service and starting it, and an installer that
/// accidentally sets a whole agent running as a side effect of storing a credential is a bad
/// installer.
/// </summary>
public sealed class EnrollCliTests : IAsyncLifetime
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "enlist-enroll-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _agentName = "enroll-agent-" + Guid.NewGuid().ToString("N")[..8];
    private ControlPlaneTestServer? _server;

    public async Task InitializeAsync()
    {
        _server = await ControlPlaneTestServer.StartAsync(TimeSpan.FromSeconds(30), AuthenticationMode.Required);
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    private string CredentialPath => Path.Combine(_dataRoot, AgentCredentialStore.FileName);

    [Fact]
    public async Task A_join_token_is_exchanged_for_a_stored_credential_and_the_verb_exits()
    {
        var token = await _server!.CreateJoinTokenAsync("--uses", "1");

        var exit = await EnrollCli.RunAsync(new[]
        {
            "enroll",
            "--control-plane", _server.BaseUri.ToString(),
            "--agent", _agentName,
            "--data", _dataRoot,
            "--join-token", token,
        });

        Assert.Equal(0, exit);
        Assert.True(File.Exists(CredentialPath), "enroll must leave a credential file behind.");

        // Readable, an agent token, and accepted by the control plane that issued it - which is the
        // whole point. A file that exists but holds the wrong thing would pass a weaker assertion.
        var stored = new AgentCredentialStore(_dataRoot).Load();
        Assert.NotNull(stored);
        Assert.StartsWith("enla_", stored);

        // /policies, because that is the endpoint an agent credential is scoped to - the same one
        // AgentEnrollment probes with. /assignments answers 403 to a perfectly good agent token,
        // which makes it a fine way to write a test that fails for the wrong reason.
        using var http = new HttpClient { BaseAddress = _server.BaseUri };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", stored);
        var response = await http.GetAsync($"/api/agents/{_agentName}/policies");
        Assert.True(response.IsSuccessStatusCode, $"the stored credential was refused: {(int)response.StatusCode}");
    }

    [Fact]
    public async Task The_join_token_is_never_echoed()
    {
        var token = await _server!.CreateJoinTokenAsync("--uses", "1");

        var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);
        try
        {
            await EnrollCli.RunAsync(new[]
            {
                "enroll",
                "--control-plane", _server.BaseUri.ToString(),
                "--agent", _agentName,
                "--data", _dataRoot,
                "--join-token", token,
            });
        }
        finally
        {
            Console.SetOut(original);
        }

        // A join token printed by the installer ends up in a bundle log, which is world-readable in
        // %TEMP%. The whole reason this verb exists is to keep the token out of places like that.
        Assert.DoesNotContain(token, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task The_credential_is_readable_by_the_account_the_service_will_run_as()
    {
        Assert.True(OperatingSystem.IsWindows());

        var token = await _server!.CreateJoinTokenAsync("--uses", "1");

        // NETWORK SERVICE stands in for "a service account that is not the installer and not an
        // administrator". Without --service-account the file's ACL is SYSTEM, Administrators and
        // whoever ran setup - so a service running as this account could not read its own credential,
        // and the install would look clean while the service refused to start.
        var exit = await EnrollCli.RunAsync(new[]
        {
            "enroll",
            "--control-plane", _server.BaseUri.ToString(),
            "--agent", _agentName,
            "--data", _dataRoot,
            "--join-token", token,
            "--service-account", "NT AUTHORITY\\NetworkService",
        });

        Assert.Equal(0, exit);

        var expected = new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null);
        var rules = new FileInfo(CredentialPath).GetAccessControl()
            .GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();

        Assert.Contains(rules, r => r.IdentityReference.Equals(expected) && r.AccessControlType == AccessControlType.Allow);

        // And still closed: inheritance off, so %ProgramData% granting Users read does not leak in.
        Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)));
    }

    [Fact]
    public async Task A_bad_join_token_fails_without_writing_a_credential()
    {
        var exit = await EnrollCli.RunAsync(new[]
        {
            "enroll",
            "--control-plane", _server!.BaseUri.ToString(),
            "--agent", _agentName,
            "--data", _dataRoot,
            "--join-token", "enlj_not_a_real_token",
        });

        Assert.Equal(2, exit);

        // Nothing half-written: the next attempt with a good token must not trip over a file that
        // exists but holds nothing usable.
        Assert.False(File.Exists(CredentialPath));
    }

    [Theory]
    [InlineData("enroll")]
    [InlineData("enroll", "--control-plane", "https://cp.example")]
    [InlineData("enroll", "--join-token", "enlj_x")]
    [InlineData("enroll", "--control-plane", "not-a-url", "--join-token", "enlj_x")]
    [InlineData("enroll", "--control-plane", "https://cp.example", "--join-token", "enlj_x", "--nonsense", "1")]
    [InlineData("enroll", "--control-plane")]
    public async Task Incomplete_or_wrong_arguments_are_refused_before_anything_is_contacted(params string[] args)
    {
        // Exit 2 and no network: every one of these is a mistake the installer could make, and each
        // should be a usage message rather than a timeout against a control plane.
        Assert.Equal(2, await EnrollCli.RunAsync(args));
        Assert.False(Directory.Exists(_dataRoot));
    }

    [Fact]
    public void The_verb_is_recognised_only_as_the_first_argument()
    {
        Assert.True(EnrollCli.IsVerb(new[] { "enroll" }));
        Assert.True(EnrollCli.IsVerb(new[] { "ENROLL", "--control-plane", "x" }));

        // --join-token on a NORMAL start still means "start the agent, enrolling on the way", which is
        // how an operator re-enrolls a machine by hand. It must not be mistaken for this verb.
        Assert.False(EnrollCli.IsVerb(new[] { "--control-plane", "x", "--join-token", "y" }));
        Assert.False(EnrollCli.IsVerb(Array.Empty<string>()));
    }
}
