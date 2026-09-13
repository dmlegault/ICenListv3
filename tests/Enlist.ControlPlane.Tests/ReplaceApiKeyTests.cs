using Enlist.ControlPlane.Contracts;
using Enlist.TestSupport;

namespace Enlist.ControlPlane.Tests;

/// <summary>
/// <c>create-api-key --replace</c> - what an installer needs and a person at a terminal does not.
///
/// The control plane's database is deliberately Permanent: it outlives an uninstall, so removing
/// enList and putting it back keeps the policy rules and packages. The consequence nobody had met
/// until a reinstall was actually tried is that the portal's key is still there, create-api-key
/// refuses a duplicate name, and the install fails on its third step.
/// </summary>
public sealed class ReplaceApiKeyTests : IAsyncLifetime
{
    private ControlPlaneTestServer? _server;

    // Required, not the default. The plain overload runs authentication Off on loopback, where every
    // request succeeds whatever key it carries - so a test asserting that a revoked key is refused
    // would pass against a server that was not checking anything at all.
    public async Task InitializeAsync() =>
        _server = await ControlPlaneTestServer.StartAsync(TimeSpan.FromSeconds(30), AuthenticationMode.Required);

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Creating_a_key_that_already_exists_is_refused_without_replace()
    {
        await _server!.RunCliAsync("create-api-key", "--name", "portal", "--role", "Operator");

        // Still the right answer for a person: silently replacing a live credential because someone
        // reused a name would be a bad surprise.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _server.RunCliAsync("create-api-key", "--name", "portal", "--role", "Operator"));

        Assert.Contains("already exists", error.Message);
    }

    [Fact]
    public async Task Replace_revokes_the_old_key_and_issues_a_new_one()
    {
        var first = await _server!.RunCliAsync("create-api-key", "--name", "portal", "--role", "Operator");
        var firstKey = System.Text.RegularExpressions.Regex.Match(first, @"enlk_\S+").Value;
        Assert.NotEmpty(firstKey);

        var second = await _server.RunCliAsync("create-api-key", "--name", "portal", "--role", "Operator", "--replace");
        var secondKey = System.Text.RegularExpressions.Regex.Match(second, @"enlk_\S+").Value;

        Assert.Contains("Revoked the existing API key 'portal'", second);
        Assert.NotEmpty(secondKey);
        Assert.NotEqual(firstKey, secondKey);

        // The old one is dead, not merely superseded. Leaving it live would be an Operator credential
        // nobody is holding, created by an install nobody thought of as issuing one.
        using var http = new HttpClient { BaseAddress = _server.BaseUri };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", firstKey);
        var refused = await http.GetAsync("/api/agents");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, refused.StatusCode);

        using var current = new HttpClient { BaseAddress = _server.BaseUri };
        current.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secondKey);
        Assert.True((await current.GetAsync("/api/agents")).IsSuccessStatusCode, "the replacement key should work");
    }

    [Fact]
    public async Task Replace_on_a_name_that_does_not_exist_yet_simply_creates_it()
    {
        // The FIRST install, which is the common case: there is nothing to revoke and that is not an
        // error. An installer that only worked on a machine enList had already been on would be a
        // strange thing to ship.
        var output = await _server!.RunCliAsync("create-api-key", "--name", "portal", "--role", "Operator", "--replace");

        Assert.DoesNotContain("Revoked", output);
        Assert.Contains("enlk_", output);
    }

    [Fact]
    public async Task A_flag_that_takes_a_value_still_has_to_have_one()
    {
        // --replace is a switch, and teaching the parser about switches must not cost the check that
        // turns a value left off by accident into a message rather than a silently missing setting.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _server!.RunCliAsync("create-api-key", "--role", "Operator", "--name"));

        Assert.Contains("needs a value", error.Message);
    }
}
