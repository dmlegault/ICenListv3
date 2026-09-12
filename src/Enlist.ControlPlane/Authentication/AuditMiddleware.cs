using System.Security.Claims;
using System.Text;

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
        // The audit line is written whether the request succeeded, was refused, or THREW. An
        // administrative write that faults is the one most worth a record, and until 2026-09-12 it
        // was the only one that produced no line at all: the exception unwound straight past the
        // logging call below.
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            WriteAuditLine(context, ex);
            throw;
        }

        WriteAuditLine(context, failure: null);
    }

    private void WriteAuditLine(HttpContext context, Exception? failure)
    {
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

        if (failure is null)
        {
            _logger.LogInformation("Audit: {Method} {Path} -> {StatusCode} by {Principal}",
                method, context.Request.Path.Value, context.Response.StatusCode, AuditIdentity.Describe(context));
            return;
        }

        // No status code is reported on this path, deliberately: the exception handler upstream has
        // not run yet, so Response.StatusCode is still whatever it was before the request failed -
        // usually 200, which would be a lie in the audit trail.
        _logger.LogWarning("Audit: {Method} {Path} -> faulted with {Failure} by {Principal}",
            method, context.Request.Path.Value, failure.GetType().Name, AuditIdentity.Describe(context));
    }
}

/// <summary>Who a request is from, in the words the audit line and CreatedBy columns use.</summary>
public static class AuditIdentity
{
    /// <summary>
    /// Long enough for DOMAIN\user and for a UPN, short enough that nothing can pad an audit line
    /// or a CreatedBy column out of readability. A name longer than this is truncated, not refused:
    /// the header is a courtesy from the portal, never an authorization input, so an odd value is
    /// worth recording as-seen rather than turning into a failed administrative call.
    /// </summary>
    private const int MaxOperatorLength = 100;

    public static string Describe(HttpContext context) => Describe(context.User, context.Request.Headers[AuditMiddleware.OperatorHeader].ToString());

    public static string Describe(ClaimsPrincipal user, string? operatorHeader)
    {
        var kind = user.FindFirstValue(EnlistClaims.Kind);
        var credential = user.FindFirstValue(EnlistClaims.Credential);
        var operatorName = Sanitize(operatorHeader);

        return kind switch
        {
            EnlistClaims.KindAgent => $"agent '{credential}'",
            EnlistClaims.KindJoin => $"join token {credential}",
            EnlistClaims.KindManagement => operatorName is null ? $"key '{credential}'" : $"key '{credential}' for {operatorName}",
            _ => operatorName is null ? "anonymous" : $"anonymous (for {operatorName})",
        };
    }

    /// <summary>
    /// X-Enlist-Operator is supplied by the CALLER, so it is attacker-controlled for anyone holding
    /// an Operator key. Written through verbatim - as it was until 2026-09-12 - a carriage return or
    /// newline in it forges whole audit lines, because the audit trail in v1 is the log
    /// (Authentication-Design.md section 6.3) and a log reader splits on newlines. Control
    /// characters are dropped and the result is capped; the value also reaches CreatedBy columns,
    /// which are nvarchar(max) and would otherwise store whatever was sent.
    /// </summary>
    private static string? Sanitize(string? operatorHeader)
    {
        if (string.IsNullOrWhiteSpace(operatorHeader))
        {
            return null;
        }

        var clean = new StringBuilder(Math.Min(operatorHeader.Length, MaxOperatorLength));
        foreach (var character in operatorHeader)
        {
            if (clean.Length == MaxOperatorLength)
            {
                break;
            }

            if (!char.IsControl(character))
            {
                clean.Append(character);
            }
        }

        var sanitized = clean.ToString().Trim();
        return sanitized.Length == 0 ? null : sanitized;
    }
}
