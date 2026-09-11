using System.Net;

using Enlist.Agent.Logging;

namespace Enlist.Agent.Credentials;

/// <summary>
/// The agent token this process presents to the control plane, held once and shared by everything
/// that talks to it: the three HttpClients (policies and packages, status, logs) through a handler
/// from <see cref="CreateHandler"/>, and the hub connection through <see cref="Token"/> as its access
/// token provider. One holder rather than a string copied into each, so the control plane rejecting
/// the credential is said once - rate-limited, with the remedy - rather than once per caller, and so
/// a credential replaced later is seen everywhere at once.
///
/// <see cref="None"/> is what an agent runs with when the control plane's authentication is Off: the
/// handler adds no header and the hub sends no token, which is exactly the anonymous call the agent
/// made before authentication existed.
///
/// The posture on rejection is the design's (Authentication-Design section 2 and 4.4): say so, keep
/// every application running, keep trying. A 401 is a header the control plane did not like; it is
/// not a reason to tear down production.
/// </summary>
public sealed class AgentCredential : IReportsDiagnostics
{
    private readonly FailureNotice _notice = new("Authentication to the control plane", TimeSpan.FromMinutes(5));
    private volatile string? _token;

    public event Action<string>? Diagnostic;

    public AgentCredential(string? token)
    {
        _token = token;
    }

    /// <summary>No credential at all - the agent calls anonymously, which the control plane accepts only when its authentication is Off.</summary>
    public static AgentCredential None => new(null);

    public string? Token => _token;

    public bool HasToken => _token is not null;

    /// <summary>
    /// A handler that presents this credential, for one HttpClient. A DelegatingHandler belongs to
    /// exactly one client, so each client gets its own; the rejection notice behind them is shared.
    /// </summary>
    public HttpMessageHandler CreateHandler() => new AgentCredentialHandler(this) { InnerHandler = new SocketsHttpHandler() };

    internal void Accepted()
    {
        if (_notice.Succeeded() is { } recovered)
        {
            Diagnostic?.Invoke(recovered);
        }
    }

    internal void Rejected(HttpStatusCode status, HttpMethod method, string? path)
    {
        var where = $"{(int)status} on {method} {path}";
        var detail = _token is null
            ? $"the control plane now requires a credential and this agent has none ({where}). " +
              "Applications keep running; nothing is reconciled until it has one. Have an Operator create a join token and restart this agent once with --join-token <token>."
            : status == HttpStatusCode.Forbidden
                ? $"the control plane accepted this agent's credential but not for this agent name ({where}) - the credential file belongs to another agent. " +
                  "Applications keep running. Delete the credential file and enroll this agent with its own join token (--join-token <token>)."
                : $"the control plane rejected this agent's credential ({where}) - it was revoked, or is unknown to this control plane. " +
                  "Applications keep running; nothing is reconciled until it is accepted again. To re-enroll, have an Operator create a join token and restart this agent once with --join-token <token>.";

        if (_notice.Failed(detail) is { } line)
        {
            Diagnostic?.Invoke(line);
        }
    }
}
