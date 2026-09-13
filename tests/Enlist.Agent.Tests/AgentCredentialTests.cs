using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

using Enlist.Agent.Configuration;
using Enlist.Agent.Credentials;
using Enlist.Agent.Status;
using Enlist.ControlPlane.Contracts;
using Enlist.TestSupport;

namespace Enlist.Agent.Tests;

/// <summary>
/// The agent's side of authentication (Authentication-Design section 4), against a real control plane
/// running with Authentication:Mode=Required - the mode every deployment gets unless it says otherwise.
/// An operator's part is played by the CLI verbs (join tokens, an Operator key, a revocation); the
/// agent's by the same classes Program.cs assembles, driven exactly as it drives them.
/// </summary>
public sealed class AgentCredentialTests : IAsyncLifetime
{
    private static readonly IReadOnlyDictionary<string, string> Required = AuthenticationMode.Required;

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "enlist-credential-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _agentName = "cred-agent-" + Guid.NewGuid().ToString("N")[..8];
    private readonly List<string> _startupLog = new();
    private ControlPlaneTestServer? _server;
    private HttpClient? _operator;
    private AgentHost? _host;

    public async Task InitializeAsync()
    {
        _server = await ControlPlaneTestServer.StartAsync(TimeSpan.FromSeconds(30), Required);
        _operator = new HttpClient { BaseAddress = _server.BaseUri };
        _operator.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await _server.CreateApiKeyAsync("tests", "Operator"));
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
        }

        _operator?.Dispose();

        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task A_first_start_with_a_join_token_enrolls_stores_the_credential_protected_and_runs_with_it()
    {
        var credential = await ResolveAsync(await _server!.CreateJoinTokenAsync("--uses", "1"));

        Assert.True(credential.HasToken);
        Assert.StartsWith("enla_", credential.Token);
        Assert.Contains(_startupLog, line => line.Contains("Enrolled as"));

        // Stored, but not in the clear: the file does not contain the token, it decrypts back to it,
        // and nobody but SYSTEM, Administrators and this account can read it - the ACL is closed, not
        // inherited from the data root.
        var store = new AgentCredentialStore(_dataRoot);
        Assert.True(File.Exists(store.Path), $"no credential file at {store.Path}");
        Assert.True(File.ReadAllBytes(store.Path).AsSpan().IndexOf(Encoding.UTF8.GetBytes(credential.Token!)) < 0, "the credential file holds the token in the clear.");
        Assert.Equal(credential.Token, store.Load());
        if (OperatingSystem.IsWindows())
        {
            var acl = new FileInfo(store.Path).GetAccessControl();
            Assert.True(acl.AreAccessRulesProtected, "the credential file inherits the data root's ACL.");

            var granted = new HashSet<string>();
            foreach (FileSystemAccessRule entry in acl.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
            {
                granted.Add(entry.IdentityReference.Value);
            }

            using var current = WindowsIdentity.GetCurrent();
            var expected = new HashSet<string>
            {
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
                current.User!.Value,
            };
            Assert.Equal(expected, granted);
        }

        // Runs with it. The re-fetch floor is an hour away, so the policy below can only arrive over
        // the hub - which proves the token reached the hub as well as the REST calls.
        var host = await StartHostAsync(credential, refetchInterval: TimeSpan.FromHours(1));
        var appName = RepoPaths.UniqueAppName();
        var rule = await CreatePolicyAsync(appName);
        await Poll.UntilAsync(() => host.Instances.ContainsKey(appName), TimeSpan.FromSeconds(30),
            "the agent never started the application: the push did not reach it, or the policy fetch was refused.");

        // Its status reports and capability reports were accepted as itself.
        await Poll.UntilAsync(async () => (await GetAgentAsync()).Capabilities?.PushChannel == PushChannelStates.Connected, TimeSpan.FromSeconds(30),
            "the control plane never accepted this agent's capability report.");
        Assert.True((await _operator!.GetAsync($"/api/agents/{_agentName}/report/latest")).IsSuccessStatusCode, "no status report from this agent reached the control plane.");

        var stop = await _operator.PutAsJsonAsync($"/api/application-policies/{rule.Id}", new UpdateApplicationPolicyRequest(null, "Stopped", null));
        stop.EnsureSuccessStatusCode();
        await Poll.UntilAsync(() => !host.Instances.ContainsKey(appName), TimeSpan.FromSeconds(30), "the second push never reached the agent.");
    }

    [Fact]
    public async Task A_later_start_uses_the_stored_credential_and_a_join_token_left_on_the_command_line_is_ignored()
    {
        var first = await ResolveAsync(await _server!.CreateJoinTokenAsync());

        var again = await ResolveAsync(joinToken: null);
        Assert.Equal(first.Token, again.Token);

        // A join token left in a service definition: the stored credential wins, the log says so, and
        // the token is not spent.
        var leftover = await _server.CreateJoinTokenAsync("--uses", "1");
        var third = await ResolveAsync(leftover);
        Assert.Equal(first.Token, third.Token);
        Assert.Contains(_startupLog, line => line.Contains("join token") && line.Contains("ignored"));
        Assert.DoesNotContain("used up", await _server.RunCliAsync("list-join-tokens"));
    }

    [Fact]
    public async Task Without_a_credential_the_agent_refuses_to_start_when_the_control_plane_requires_one()
    {
        var refused = await Assert.ThrowsAsync<AgentStartupException>(() => ResolveAsync(joinToken: null));

        Assert.Contains("requires a credential", refused.Message);
        Assert.Contains("--join-token", refused.Message);
        Assert.False(File.Exists(new AgentCredentialStore(_dataRoot).Path));
    }

    [Fact]
    public async Task Without_a_credential_the_agent_runs_anonymously_when_authentication_is_off()
    {
        // The default test server: Off, on loopback - the demo's shape.
        await using var off = await ControlPlaneTestServer.StartAsync(TimeSpan.FromSeconds(30));

        var credential = await AgentEnrollment.ResolveAsync(off.BaseUri, _agentName, new AgentCredentialStore(_dataRoot), null, Log, CancellationToken.None);

        Assert.False(credential.HasToken);
        Assert.Contains(_startupLog, line => line.Contains("authentication is Off"));
    }

    [Fact]
    public async Task A_revoked_credential_leaves_applications_running_is_reported_once_and_is_replaced_by_re_enrolling()
    {
        var credential = await ResolveAsync(await _server!.CreateJoinTokenAsync());
        var host = await StartHostAsync(credential, refetchInterval: TimeSpan.FromSeconds(2));
        var appName = RepoPaths.UniqueAppName();
        await CreatePolicyAsync(appName);
        await Poll.UntilAsync(() => host.Instances.ContainsKey(appName), TimeSpan.FromSeconds(30), "the application never started.");

        await _server.RunCliAsync("revoke-agent", "--name", _agentName);

        // From the next fetch on, every call is refused. The application is untouched, and the agent
        // log says why - once, with the remedy, not once per refused call.
        await Poll.UntilAsync(() => AgentLog().Contains("Authentication to the control plane failed"), TimeSpan.FromSeconds(30),
            "the agent never said its credential was rejected.");
        await Task.Delay(TimeSpan.FromSeconds(6));
        Assert.True(host.Instances.ContainsKey(appName), "a rejected credential stopped a running application.");
        Assert.Single(Regex.Matches(AgentLog(), "Authentication to the control plane failed"));
        Assert.Contains("--join-token", AgentLog());

        // A restart with nothing but the stored credential cannot proceed, and says what to do.
        var refused = await Assert.ThrowsAsync<AgentStartupException>(() => ResolveAsync(joinToken: null));
        Assert.Contains("rejected", refused.Message);
        Assert.Contains("--join-token", refused.Message);

        // With a new join token it re-enrolls: a different token, stored in place of the old one, accepted.
        var renewed = await ResolveAsync(await _server.CreateJoinTokenAsync());
        Assert.NotEqual(credential.Token, renewed.Token);
        Assert.Equal(renewed.Token, new AgentCredentialStore(_dataRoot).Load());
        using var asAgent = new HttpClient(renewed.CreateHandler()) { BaseAddress = _server.BaseUri };
        Assert.True((await asAgent.GetAsync($"/api/agents/{_agentName}/policies")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task A_credential_file_this_machine_cannot_read_is_reported_not_treated_as_never_enrolled()
    {
        var store = new AgentCredentialStore(_dataRoot);
        Directory.CreateDirectory(_dataRoot);
        File.WriteAllBytes(store.Path, [1, 2, 3, 4, 5, 6, 7, 8]);

        var refused = await Assert.ThrowsAsync<AgentStartupException>(() => ResolveAsync(joinToken: null));

        Assert.Contains(store.Path, refused.Message);
        Assert.Contains("another machine", refused.Message);
    }

    private Task<AgentCredential> ResolveAsync(string? joinToken) =>
        AgentEnrollment.ResolveAsync(_server!.BaseUri, _agentName, new AgentCredentialStore(_dataRoot), joinToken, Log, CancellationToken.None);

    private void Log(string line)
    {
        lock (_startupLog)
        {
            _startupLog.Add(line);
        }
    }

    private async Task<AgentHost> StartHostAsync(AgentCredential credential, TimeSpan refetchInterval)
    {
        var assignmentSource = new ControlPlaneAssignmentSource(_server!.BaseUri, _agentName, Path.Combine(_dataRoot, "Packages"), refetchInterval, credential);
        await assignmentSource.ConnectAsync(CancellationToken.None);

        var statusReporter = new ControlPlaneStatusReporter(new HttpClient(credential.CreateHandler()) { BaseAddress = _server.BaseUri }, _agentName);

        _host = new AgentHost(assignmentSource, RepoPaths.RunnerBinDirectory(), Path.Combine(_dataRoot, "Runners"), Path.Combine(_dataRoot, "Logs"), statusReporter: statusReporter);
        await _host.StartAsync();
        return _host;
    }

    private async Task<ApplicationPolicyDto> CreatePolicyAsync(string appName)
    {
        var request = new CreateApplicationPolicyRequest(
            appName, RepoPaths.SampleServiceDir(), "Running", null,
            TagSelector: new Dictionary<string, string> { ["agent"] = _agentName });
        var response = await _operator!.PostAsJsonAsync("/api/application-policies", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApplicationPolicyDto>())!;
    }

    private async Task<AgentDto> GetAgentAsync()
    {
        var agents = await _operator!.GetFromJsonAsync<List<AgentDto>>("/api/agents");
        return agents!.Single(a => a.Name == _agentName);
    }

    private string AgentLog()
    {
        var logs = Path.Combine(_dataRoot, "Logs");
        return Directory.Exists(logs)
            ? string.Join(Environment.NewLine, Directory.GetFiles(logs, "agent-*.log").Select(File.ReadAllText))
            : "";
    }
}
