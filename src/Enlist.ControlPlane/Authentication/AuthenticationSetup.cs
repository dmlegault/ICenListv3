using Enlist.ControlPlane.Contracts;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Enlist.ControlPlane.Authentication;

/// <summary>Wires the scheme, the fallback policy, the startup rules and the audit line. Two calls from Program.cs; everything else in this folder is behind them.</summary>
public static class AuthenticationSetup
{
    public static void AddEnlistAuthentication(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(AuthenticationOptions.SectionName);
        builder.Services.Configure<AuthenticationOptions>(section);
        var options = section.Get<AuthenticationOptions>() ?? new AuthenticationOptions();

        builder.Services
            .AddAuthentication(EnlistBearerHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, EnlistBearerHandler>(EnlistBearerHandler.SchemeName, displayName: null, configureOptions: null);

        builder.Services.AddSingleton<IAuthorizationHandler, EndpointPolicyHandler>();
        builder.Services.AddAuthorization(authorization =>
        {
            // Off — loopback only, enforced at startup below — has no fallback policy at all, so
            // every endpoint is anonymous exactly as it was before authentication existed. Required
            // puts the table in front of everything that does not carry its own policy metadata.
            if (options.IsRequired)
            {
                authorization.FallbackPolicy = new AuthorizationPolicyBuilder(EnlistBearerHandler.SchemeName)
                    .AddRequirements(EndpointPolicyRequirement.Instance)
                    .Build();
            }
        });
    }

    /// <summary>
    /// The hard rules first — a configuration that would expose an unauthenticated control plane, or
    /// send tokens over plain HTTP, is refused before the database is even consulted — then the
    /// middleware, then the audit line on every administrative write. Throws so that under the SCM
    /// the reason lands in the Event Log like the migration checks that follow it.
    /// </summary>
    public static void UseEnlistAuthentication(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
        if (ListenerRules.Violation(ListenerRules.ConfiguredUrls(app.Configuration), options.Mode, "control plane") is { } violation)
        {
            throw new InvalidOperationException(violation);
        }

        // Order matters, and it was wrong until 2026-09-13: the audit middleware sat AFTER
        // UseAuthorization, which short-circuits a refused request without ever calling the next
        // middleware. Every 401 and 403 therefore left no audit line at all - and an attempt to do
        // something one is not permitted to do is the single most interesting thing an audit trail
        // can hold. Found by writing the test for it (CoverageGapTests).
        //
        // Between the two is the only correct place. After UseAuthentication, so the line can name
        // WHO was refused; before UseAuthorization, so the refusal comes back THROUGH it.
        app.UseAuthentication();
        app.UseMiddleware<AuditMiddleware>();
        app.UseAuthorization();
    }
}
