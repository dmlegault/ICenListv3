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
        if (ListenerRules.Violation(ConfiguredUrls(app.Configuration), options.Mode, "control plane") is { } violation)
        {
            throw new InvalidOperationException(violation);
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<AuditMiddleware>();
    }

    /// <summary>Every place a listen address can come from: --urls / ASPNETCORE_URLS (the "urls" key) and Kestrel endpoint configuration.</summary>
    public static IReadOnlyList<string> ConfiguredUrls(IConfiguration configuration)
    {
        var urls = new List<string>();

        if (configuration["urls"] is { } fromUrls && !string.IsNullOrWhiteSpace(fromUrls))
        {
            urls.AddRange(fromUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            if (endpoint["Url"] is { } url && !string.IsNullOrWhiteSpace(url))
            {
                urls.Add(url);
            }
        }

        return urls;
    }
}
