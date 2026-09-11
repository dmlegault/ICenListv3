using System.Security.Claims;

using Enlist.ControlPlane.Contracts;

namespace Enlist.ControlPlane.Authentication;

/// <summary>
/// One structured line per administrative write, after it completes: method, path, status, and WHO —
/// the key name plus the X-Enlist-Operator header the portal sends (the Windows identity of the
/// person whose action this is), the agent name, or "anonymous" under Off. Authentication-Design.md
/// section 6.3: the existing logging pipeline (and the Event Log under the SCM) is the audit trail in
/// v1; a table is listed as the natural next step.
///
/// Reads are not audited, and neither is what an agent does as itself — status reports, log batches,
/// capability reports arrive every few seconds from every agent and are telemetry, not
/// administration. Enrollment is audited: it is the one thing a join token does.
/// </summary>
public sealed class AuditMiddleware
{
    public const string OperatorHeader = "X-Enlist-Operator";

    private readonly RequestDelegate _next;
    private readonly ILogger<AuditMiddleware> _logger;

    public AuditMiddleware(RequestDelegate next, ILogger<AuditMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        await _next(context);

        var method = context.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
        {
            return;
        }

        if (context.GetEndpoint() is RouteEndpoint endpoint && endpoint.RoutePattern.RawText is { } pattern)
        {
            if (EndpointPolicies.IsHub(pattern) || EndpointPolicies.For(method, pattern) == EndpointPolicy.Agent)
            {
                return;
            }
        }

        _logger.LogInformation("Audit: {Method} {Path} -> {StatusCode} by {Principal}",
            method, context.Request.Path.Value, context.Response.StatusCode, AuditIdentity.Describe(context));
    }
}

/// <summary>Who a request is from, in the words the audit line and CreatedBy columns use.</summary>
public static class AuditIdentity
{
    public static string Describe(HttpContext context) => Describe(context.User, context.Request.Headers[AuditMiddleware.OperatorHeader].ToString());

    public static string Describe(ClaimsPrincipal user, string? operatorHeader)
    {
        var kind = user.FindFirstValue(EnlistClaims.Kind);
        var credential = user.FindFirstValue(EnlistClaims.Credential);
        var operatorName = string.IsNullOrWhiteSpace(operatorHeader) ? null : operatorHeader.Trim();

        return kind switch
        {
            EnlistClaims.KindAgent => $"agent '{credential}'",
            EnlistClaims.KindJoin => $"join token {credential}",
            EnlistClaims.KindManagement => operatorName is null ? $"key '{credential}'" : $"key '{credential}' for {operatorName}",
            _ => operatorName is null ? "anonymous" : $"anonymous (for {operatorName})",
        };
    }
}
