using System.Net;
using System.Net.Http.Headers;

namespace Enlist.Agent.Credentials;

/// <summary>
/// Puts the agent's credential on every request as <c>Authorization: Bearer</c>, the one way every
/// enList token is presented, and tells the shared <see cref="AgentCredential"/> when the control
/// plane refuses it (401 or 403) and when it is accepted again. Nothing else: the callers' own
/// best-effort handling (status reports and log batches that fail say so and carry on; a policy fetch
/// that fails is retried on the next change signal) is unchanged, because a rejected credential is
/// just one more way a call to the control plane can fail.
/// </summary>
internal sealed class AgentCredentialHandler : DelegatingHandler
{
    private readonly AgentCredential _credential;

    public AgentCredentialHandler(AgentCredential credential)
    {
        _credential = credential;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_credential.Token is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _credential.Rejected(response.StatusCode, request.Method, request.RequestUri?.AbsolutePath);
        }
        else if (response.IsSuccessStatusCode)
        {
            _credential.Accepted();
        }

        return response;
    }
}
