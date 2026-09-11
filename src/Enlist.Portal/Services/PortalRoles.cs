using System.Security.Claims;
using System.Security.Principal;

using Enlist.ControlPlane.Contracts;

using Microsoft.AspNetCore.Authentication;

namespace Enlist.Portal.Services;

/// <summary>
/// Turns a Windows identity into the two roles the portal knows, once per request, while the
/// identity's token is still alive. Group membership is checked by name (<c>WindowsPrincipal.IsInRole</c>
/// resolves "CORP\enList Operators" or "BUILTIN\Administrators" through the local security
/// authority, so domain and local groups both work) and the result is a NEW, detached principal
/// carrying only a name and role claims. Detached on purpose: the principal travels into the Blazor
/// circuit and outlives the HTTP request that authenticated it, and a WindowsIdentity's group list
/// is backed by an access token that does not. Every policy in the portal is then a plain
/// RequireRole, which AuthorizeView, IAuthorizationService and [Authorize] all understand.
/// </summary>
public sealed class PortalRoles : IClaimsTransformation
{
    public const string Operator = ManagementRoles.Operator;
    public const string Viewer = ManagementRoles.Viewer;

    private readonly PortalAuthenticationOptions _options;

    public PortalRoles(PortalAuthenticationOptions options)
    {
        _options = options;
    }

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (!OperatingSystem.IsWindows() || principal.Identity is not WindowsIdentity windows || !windows.IsAuthenticated)
        {
            return Task.FromResult(principal);
        }

        var identity = new ClaimsIdentity(windows.AuthenticationType, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.Name, windows.Name));
        foreach (var role in Resolve(new WindowsPrincipal(windows), _options.Windows))
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }

        return Task.FromResult(new ClaimsPrincipal(identity));
    }

    /// <summary>Operator beats Viewer; a person in both groups is an Operator. A group that is not configured, or does not exist, admits nobody.</summary>
    public static IReadOnlyList<string> Resolve(IPrincipal principal, PortalAuthenticationOptions.WindowsGroups groups)
    {
        var roles = new List<string>(2);
        if (IsMember(principal, groups.OperatorsGroup))
        {
            roles.Add(Operator);
        }

        if (IsMember(principal, groups.ViewersGroup))
        {
            roles.Add(Viewer);
        }

        return roles;
    }

    private static bool IsMember(IPrincipal principal, string? group)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            return false;
        }

        try
        {
            return principal.IsInRole(group.Trim());
        }
        catch (Exception)
        {
            // A group name the local security authority cannot resolve (a typo, an unreachable
            // domain) admits nobody rather than everybody.
            return false;
        }
    }
}
