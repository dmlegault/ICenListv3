using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Principal;

using Enlist.Portal.Services;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Enlist.Portal.Tests;

/// <summary>
/// The two things a Windows sign-in turns into, pinned without the handshake (which
/// PortalAuthenticationTests can only exercise on a machine that lets an account sign in to itself):
/// a Windows identity becomes a detached principal carrying exactly the roles its groups earn, and the
/// portal's policies - Required and Off - admit exactly who they should. The account running the
/// tests is in "Authenticated Users" and not in "Guests".
/// </summary>
public sealed class PortalRolesTests
{
    private const string Everyone = @"NT AUTHORITY\Authenticated Users";
    private const string Nobody = @"BUILTIN\Guests";

    [SkippableFact]
    [SupportedOSPlatform("windows")] // Skip.IfNot below is the runtime guard; this is the one the analyzer reads.
    public async Task A_windows_identity_becomes_a_detached_principal_with_the_roles_its_groups_earn()
    {
        // Skip, not an early return. Returning made this test PASS on a machine where it never ran a
        // single assertion, which is worse than no test: a green tick that means nothing.
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows identities and group membership are a Windows facility.");

        using var windows = WindowsIdentity.GetCurrent();
        var options = new PortalAuthenticationOptions { Windows = { OperatorsGroup = Everyone, ViewersGroup = Nobody } };

        var principal = await new PortalRoles(options).TransformAsync(new ClaimsPrincipal(windows));

        Assert.Equal(windows.Name, principal.Identity?.Name);
        Assert.True(principal.IsInRole(PortalRoles.Operator));
        Assert.False(principal.IsInRole(PortalRoles.Viewer));
        Assert.True(principal.Identity?.IsAuthenticated);

        // Detached: no WindowsIdentity, no access token, nothing that dies with the request.
        Assert.IsNotType<WindowsIdentity>(principal.Identity);
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void Group_membership_decides_the_roles_and_an_unknown_or_unconfigured_group_admits_nobody()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows identities and group membership are a Windows facility.");

        using var windows = WindowsIdentity.GetCurrent();
        var me = new WindowsPrincipal(windows);

        Assert.Equal([PortalRoles.Operator], PortalRoles.Resolve(me, new() { OperatorsGroup = Everyone, ViewersGroup = Nobody }));
        Assert.Equal([PortalRoles.Viewer], PortalRoles.Resolve(me, new() { OperatorsGroup = Nobody, ViewersGroup = Everyone }));
        Assert.Equal([PortalRoles.Operator, PortalRoles.Viewer], PortalRoles.Resolve(me, new() { OperatorsGroup = Everyone, ViewersGroup = Everyone }));
        Assert.Empty(PortalRoles.Resolve(me, new() { OperatorsGroup = Nobody, ViewersGroup = Nobody }));
        Assert.Empty(PortalRoles.Resolve(me, new()));
        Assert.Empty(PortalRoles.Resolve(me, new() { OperatorsGroup = @"NOSUCHDOMAIN\No Such Group" }));
    }

    [Fact]
    public async Task Under_required_the_policies_admit_by_role_and_nobody_anonymous()
    {
        var authorization = Authorization(new PortalAuthenticationOptions { Mode = "Required" });

        Assert.True((await authorization.AuthorizeAsync(User("alice", PortalRoles.Operator), PortalRoles.Operator)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(User("alice", PortalRoles.Operator), PortalRoles.Viewer)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(User("bob", PortalRoles.Viewer), PortalRoles.Operator)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(User("bob", PortalRoles.Viewer), PortalRoles.Viewer)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(User("carol"), PortalRoles.Viewer)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), PortalRoles.Viewer)).Succeeded);
    }

    [Fact]
    public async Task Under_off_both_policies_admit_everyone_including_anonymous()
    {
        var authorization = Authorization(new PortalAuthenticationOptions { Mode = "Off" });
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.True((await authorization.AuthorizeAsync(anonymous, PortalRoles.Operator)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(anonymous, PortalRoles.Viewer)).Succeeded);
    }

    /// <summary>The real registration (PortalAuthentication.AddPortalAuthentication), on a real builder, never run.</summary>
    private static IAuthorizationService Authorization(PortalAuthenticationOptions options)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRazorComponents();
        builder.AddPortalAuthentication(options);
        return builder.Build().Services.GetRequiredService<IAuthorizationService>();
    }

    private static ClaimsPrincipal User(string name, params string[] roles)
    {
        var identity = new ClaimsIdentity("Negotiate", ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.Name, name));
        foreach (var role in roles)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(identity);
    }
}
