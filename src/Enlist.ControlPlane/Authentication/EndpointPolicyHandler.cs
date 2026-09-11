using System.Security.Claims;

using Enlist.ControlPlane.Contracts;

using Microsoft.AspNetCore.Authorization;

namespace Enlist.ControlPlane.Authentication;

/// <summary>The single requirement on the fallback policy; <see cref="EndpointPolicyHandler"/> decides it from the table.</summary>
public sealed class EndpointPolicyRequirement : IAuthorizationRequirement
{
    public static readonly EndpointPolicyRequirement Instance = new();
}

/// <summary>
/// Evaluates <see cref="EndpointPolicies"/> for the endpoint being called. Not succeeding is the
/// refusal: the authorization middleware turns that into a 401 for a caller with no credential and a
/// 403 for one with the wrong kind or role. Every path that cannot decide — no HttpContext, no route
/// endpoint, a route not in the table — falls through without succeeding, which is the fail-closed
/// default the table is designed around.
/// </summary>
public sealed class EndpointPolicyHandler : AuthorizationHandler<EndpointPolicyRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, EndpointPolicyRequirement requirement)
    {
        if (context.Resource is not HttpContext http || http.GetEndpoint() is not RouteEndpoint endpoint || endpoint.RoutePattern.RawText is not { } pattern)
        {
            return Task.CompletedTask;
        }

        var user = context.User;
        var kind = user.FindFirstValue(EnlistClaims.Kind);

        bool allowed;
        if (EndpointPolicies.IsHub(pattern))
        {
            allowed = kind == EnlistClaims.KindAgent;
        }
        else
        {
            allowed = EndpointPolicies.For(http.Request.Method, pattern) switch
            {
                EndpointPolicy.Anonymous => true,
                EndpointPolicy.JoinToken => kind == EnlistClaims.KindJoin,
                EndpointPolicy.Agent => kind == EnlistClaims.KindAgent && AgentNameMatchesRoute(http, user),
                EndpointPolicy.AgentOrViewer => kind is EnlistClaims.KindAgent or EnlistClaims.KindManagement,
                EndpointPolicy.Viewer => kind == EnlistClaims.KindManagement,
                EndpointPolicy.Operator => kind == EnlistClaims.KindManagement && user.FindFirstValue(EnlistClaims.Role) == ManagementRoles.Operator,
                _ => false,
            };
        }

        if (allowed)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    /// <summary>The name binding: the agent in the URL must be the agent in the credential. Case-insensitive, because that is how the database compares names.</summary>
    private static bool AgentNameMatchesRoute(HttpContext http, ClaimsPrincipal user)
    {
        var routeName = http.Request.RouteValues["agentName"] as string ?? http.Request.RouteValues["name"] as string;
        var enrolledName = user.FindFirstValue(EnlistClaims.Agent);
        return routeName is not null && enrolledName is not null && string.Equals(routeName, enrolledName, StringComparison.OrdinalIgnoreCase);
    }
}
