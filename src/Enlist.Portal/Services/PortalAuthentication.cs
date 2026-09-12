using Enlist.ControlPlane.Contracts;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;

namespace Enlist.Portal.Services;

/// <summary>
/// Wires Windows authentication and the two roles, or - under Off, loopback only - nothing, with
/// the same policy names so every AuthorizeView and [Authorize] in the components reads the same in
/// both modes. Two calls from Program.cs.
/// </summary>
public static class PortalAuthentication
{
    public static void AddPortalAuthentication(this WebApplicationBuilder builder, PortalAuthenticationOptions options)
    {
        builder.Services.AddSingleton(options);
        builder.Services.AddCascadingAuthenticationState();

        if (options.IsRequired)
        {
            // Negotiate on Kestrel: Kerberos in a domain (the service account needs an HTTP/<fqdn> SPN
            // unless it runs as a machine account), NTLM against local accounts on a workgroup machine.
            // Under IIS, Windows Authentication is configured on the site instead and this handler
            // steps aside.
            builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
            builder.Services.AddTransient<IClaimsTransformation, PortalRoles>();
            builder.Services.AddAuthorization(authorization =>
            {
                authorization.AddPolicy(PortalRoles.Operator, policy => policy.RequireAuthenticatedUser().RequireRole(PortalRoles.Operator));
                authorization.AddPolicy(PortalRoles.Viewer, policy => policy.RequireAuthenticatedUser().RequireRole(PortalRoles.Operator, PortalRoles.Viewer));
            });
        }
        else
        {
            // No scheme, so every request is anonymous, and both policies admit everyone: the portal
            // behaves exactly as it did before authentication existed. Permitted on loopback only.
            builder.Services.AddAuthentication();
            builder.Services.AddAuthorization(authorization =>
            {
                authorization.AddPolicy(PortalRoles.Operator, policy => policy.RequireAssertion(_ => true));
                authorization.AddPolicy(PortalRoles.Viewer, policy => policy.RequireAssertion(_ => true));
            });
        }
    }

    /// <summary>The hard rules first (R3: the portal has the same two), then the middleware. Throws so that under the SCM the reason lands in the Event Log.</summary>
    public static void UsePortalAuthentication(this WebApplication app, PortalAuthenticationOptions options)
    {
        if (ListenerRules.Violation(ListenerRules.ConfiguredUrls(app.Configuration), options.Mode, "portal") is { } violation)
        {
            throw new InvalidOperationException(violation);
        }

        app.UseAuthentication();
        app.UseAuthorization();
    }
}
