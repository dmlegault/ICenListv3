using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Enlist.ControlPlane.Contracts;

namespace Enlist.Agent.Credentials;

/// <summary>A reason the agent will not start, in words an operator can act on. Program.cs prints it, writes it to the agent log, and exits 2.</summary>
public sealed class AgentStartupException : Exception
{
    public AgentStartupException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Settles, before anything else is built, what this agent presents to the control plane
/// (Authentication-Design section 4). In order:
///
///   1. A stored credential is checked against the control plane. Accepted (or the control plane is
///      not answering, in which case the connect that follows will say so): use it - and if a join
///      token is also on the command line, say that it was ignored, because a join token left in a
///      service definition is a secret in `sc qc`. Rejected: re-enroll if there is a join token to do
///      it with, otherwise refuse to start and say what to do.
///   2. No stored credential and a join token: enroll - POST /api/agents/enroll with the join token as
///      the bearer - store the agent token it returns, and never need the join token again.
///   3. Neither: ask GET /health which mode the control plane is in. Required is a refusal to start
///      with the remedy; Off means the agent runs anonymously, which is the demo.
///
/// Every outcome goes through <paramref name="log"/> - the agent log and the console - because the
/// service manager shows none of this, and "the service failed to start" is where the reason has to
/// be written down.
/// </summary>
public static class AgentEnrollment
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static async Task<AgentCredential> ResolveAsync(Uri controlPlane, string agentName, AgentCredentialStore store, string? joinToken, Action<string> log, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = controlPlane, Timeout = Timeout };

        var stored = store.Load();
        if (stored is not null)
        {
            var (verdict, reason) = await ProbeAsync(http, stored, agentName, ct).ConfigureAwait(false);

            if (verdict == Verdict.Rejected)
            {
                if (joinToken is null)
                {
                    throw new AgentStartupException(
                        $"The control plane at {controlPlane} rejected this agent's stored credential ({reason}). If it was revoked, have an Operator create a join token " +
                        $"and start this agent once with --join-token <token>; the new credential replaces the one in {store.Path}.");
                }

                log($"The control plane rejected this agent's stored credential ({reason}); enrolling again with the join token.");
            }
            else
            {
                if (verdict == Verdict.Unreachable)
                {
                    log($"The control plane at {controlPlane} is not answering ({reason}), so the stored credential could not be checked; continuing with it.");
                }

                if (joinToken is not null)
                {
                    log($"A join token is on the command line, but this agent already holds a credential ({store.Path}) and the join token was ignored. " +
                        "Remove it from the service definition: it is only for the first start, and sc qc shows it to anyone who can query the service.");
                }

                return new AgentCredential(stored);
            }
        }

        if (joinToken is not null)
        {
            var token = await EnrollAsync(http, joinToken, agentName, ct).ConfigureAwait(false);
            store.Save(token);
            log($"Enrolled as '{agentName}'. The credential is stored at {store.Path}; --join-token is not needed again.");
            return new AgentCredential(token);
        }

        var mode = await ModeAsync(http, ct).ConfigureAwait(false);
        if (string.Equals(mode, "Required", StringComparison.OrdinalIgnoreCase))
        {
            throw new AgentStartupException(
                $"The control plane at {controlPlane} requires a credential and this agent has none. Have an Operator create a join token " +
                $"(Enlist.ControlPlane create-join-token) and start this agent once with --join-token <token>; the credential is then stored at {store.Path} " +
                "and the option is not needed again.");
        }

        log(mode is null
            ? $"The control plane at {controlPlane} did not say whether it requires a credential; starting without one."
            : $"Control plane authentication is {mode}; running without a credential.");
        return AgentCredential.None;
    }

    private enum Verdict
    {
        Accepted,
        Rejected,
        Unreachable,
    }

    /// <summary>The cheapest call an agent makes as itself: its own policy list. 401 and 403 are the credential's fault; anything else is the control plane's, and not this method's to judge.</summary>
    private static async Task<(Verdict Verdict, string? Reason)> ProbeAsync(HttpClient http, string token, string agentName, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/agents/{Uri.EscapeDataString(agentName)}/policies");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => (Verdict.Rejected, "401: unknown to this control plane, or revoked"),
                HttpStatusCode.Forbidden => (Verdict.Rejected, $"403: the credential is not for '{agentName}' - it belongs to another agent"),
                _ when response.IsSuccessStatusCode => (Verdict.Accepted, null),
                _ => (Verdict.Unreachable, $"HTTP {(int)response.StatusCode}"),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (Verdict.Unreachable, TransportFailure.Describe(ex));
        }
    }

    private static async Task<string> EnrollAsync(HttpClient http, string joinToken, string agentName, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/agents/enroll")
        {
            Content = JsonContent.Create(new EnrollAgentRequest(agentName)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", joinToken);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AgentStartupException($"Could not reach the control plane at {http.BaseAddress} to enroll: {TransportFailure.Describe(ex)}");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Created)
            {
                var enrolled = await response.Content.ReadFromJsonAsync<EnrollAgentResponse>(ct).ConfigureAwait(false);
                if (enrolled?.AgentToken is not { Length: > 0 } token)
                {
                    throw new AgentStartupException("The control plane accepted the enrollment but returned no agent token.");
                }

                return token;
            }

            var body = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim().Trim('"');
            throw new AgentStartupException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "The join token was refused: it is unknown to this control plane, expired, revoked or used up. Have an Operator create another (Enlist.ControlPlane create-join-token).",
                HttpStatusCode.Conflict => $"Enrollment as '{agentName}' was refused: {body}",
                HttpStatusCode.BadRequest => $"Enrollment as '{agentName}' was refused: {body}",
                _ => $"Enrollment as '{agentName}' failed with HTTP {(int)response.StatusCode}: {body}",
            });
        }
    }

    /// <summary>"Required", "Off", or null when /health did not answer with a mode. Read as a document rather than a DTO so a 503 (database down) still yields the mode it carries.</summary>
    private static async Task<string?> ModeAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync("/health", ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return document.RootElement.TryGetProperty("authentication", out var mode) && mode.ValueKind == JsonValueKind.String
                ? mode.GetString()
                : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
