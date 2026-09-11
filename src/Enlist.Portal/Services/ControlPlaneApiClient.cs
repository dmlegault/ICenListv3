using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;

using Enlist.ControlPlane.Contracts;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace Enlist.Portal.Services;

/// <summary>
/// The only thing the portal talks to (design doc section 8: "The portal never talks to an agent").
/// Thin wrapper over Enlist.ControlPlane's REST API — no business logic of its own, just typed calls
/// plus turning a non-success response into an ApiException pages can catch and show via Snackbar.
///
/// Two headers go on every request (Authentication-Design.md 6.1): the portal's own Operator key as
/// the bearer, and X-Enlist-Operator naming the Windows identity of the person whose action this is,
/// which the control plane logs on every write. And one check before any write leaves: the person
/// must be an Operator here. The key is Operator-tier regardless of who is looking at the page, so
/// the portal is where a Viewer's write is refused - the control plane cannot tell the two apart.
/// </summary>
public sealed class ControlPlaneApiClient
{
    public const string OperatorHeader = "X-Enlist-Operator";

    private readonly HttpClient _http;
    private readonly PortalCredential? _credential;
    private readonly AuthenticationStateProvider? _authentication;
    private readonly IAuthorizationService? _authorization;

    public ControlPlaneApiClient(HttpClient http, PortalCredential? credential = null, AuthenticationStateProvider? authentication = null, IAuthorizationService? authorization = null)
    {
        _http = http;
        _credential = credential;
        _authentication = authentication;
        _authorization = authorization;
    }

    public async Task<List<AgentDto>> GetAgentsAsync(CancellationToken ct = default) =>
        await SendAsync<List<AgentDto>>(new HttpRequestMessage(HttpMethod.Get, "/api/agents"), ct).ConfigureAwait(false) ?? [];

    public Task<AgentDto> SetAgentTagsAsync(string agentName, IReadOnlyDictionary<string, string> tags, CancellationToken ct = default) =>
        SendAsync<AgentDto>(new HttpRequestMessage(HttpMethod.Put, $"/api/agents/{Uri.EscapeDataString(agentName)}/tags") { Content = JsonContent.Create(new SetAgentTagsRequest(tags)) }, ct)!;

    /// <summary>Never blocked: a rule targets tags, not an agent, so nothing references the row — it simply reappears on the agent's next contact. Deleting also revokes the agent's credential, so under Required it cannot reappear without a new join token.</summary>
    public Task DeleteAgentAsync(string agentName, CancellationToken ct = default) =>
        SendAsync<object>(new HttpRequestMessage(HttpMethod.Delete, $"/api/agents/{Uri.EscapeDataString(agentName)}"), ct);

    /// <summary>The agent keeps running what it runs; its next call is refused, and the name is free to enroll again with a new join token.</summary>
    public Task RevokeAgentCredentialAsync(string agentName, CancellationToken ct = default) =>
        SendAsync<object>(new HttpRequestMessage(HttpMethod.Delete, $"/api/agents/{Uri.EscapeDataString(agentName)}/credential"), ct);

    /// <summary>False excludes this agent from scheduling entirely — see AgentDto.SchedulingEnabled.</summary>
    public Task<AgentDto> SetAgentSchedulingAsync(string agentName, bool schedulingEnabled, CancellationToken ct = default) =>
        SendAsync<AgentDto>(new HttpRequestMessage(HttpMethod.Put, $"/api/agents/{Uri.EscapeDataString(agentName)}/scheduling") { Content = JsonContent.Create(new SetAgentSchedulingRequest(schedulingEnabled)) }, ct)!;

    public async Task<List<ApplicationPolicyDto>> GetApplicationPoliciesAsync(string? agentName = null, CancellationToken ct = default)
    {
        var url = agentName is null ? "/api/application-policies" : $"/api/application-policies?agentName={Uri.EscapeDataString(agentName)}";
        return await SendAsync<List<ApplicationPolicyDto>>(new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false) ?? [];
    }

    public Task<ApplicationPolicyDto> CreateApplicationPolicyAsync(CreateApplicationPolicyRequest request, CancellationToken ct = default) =>
        SendAsync<ApplicationPolicyDto>(new HttpRequestMessage(HttpMethod.Post, "/api/application-policies") { Content = JsonContent.Create(request) }, ct)!;

    public Task<ApplicationPolicyDto> UpdateApplicationPolicyAsync(Guid id, UpdateApplicationPolicyRequest request, CancellationToken ct = default) =>
        SendAsync<ApplicationPolicyDto>(new HttpRequestMessage(HttpMethod.Put, $"/api/application-policies/{id}") { Content = JsonContent.Create(request) }, ct)!;

    public Task DeleteApplicationPolicyAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<object>(new HttpRequestMessage(HttpMethod.Delete, $"/api/application-policies/{id}"), ct);

    public async Task<List<PackageInfo>> GetPackagesAsync(CancellationToken ct = default) =>
        await SendAsync<List<PackageInfo>>(new HttpRequestMessage(HttpMethod.Get, "/api/packages"), ct).ConfigureAwait(false) ?? [];

    /// <summary>The portal's own path onto the same endpoint enlist-deploy posts to — a raw zip body, not
    /// multipart form data, matching exactly what the CLI already sends (see Enlist.Deploy's own
    /// UploadPackageAsync). Uploading under an application name that already has packages just adds
    /// another version alongside them; it never touches any existing policy rule (see PackageEntity /
    /// the endpoint's own comment) — the caller is expected to know that and surface it, this method
    /// doesn't decide anything about it.</summary>
    public async Task<UploadPackageResponse> UploadPackageAsync(string applicationName, Stream content, CancellationToken ct = default)
    {
        var url = $"/api/packages?application={Uri.EscapeDataString(applicationName)}";
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StreamContent(content) };
        return (await SendAsync<UploadPackageResponse>(request, ct).ConfigureAwait(false))!;
    }

    /// <summary>Strict-blocked server-side (409) if any policy rule still references this digest — see the endpoint's own comment. The portal disables the delete button for the same reason before this is ever called, but the check is authoritative here, not there.</summary>
    public Task DeletePackageAsync(string digest, CancellationToken ct = default) =>
        SendAsync<object>(new HttpRequestMessage(HttpMethod.Delete, $"/api/packages/{Uri.EscapeDataString(digest)}"), ct);

    /// <summary>The static "what's in this package" catalog — a metadata-only scan, not a live status read, so it's available the moment a package is uploaded regardless of whether any agent has run it. Null on 404 (unknown digest), same "not fatal, just unknown" posture as GetLatestReportAsync.</summary>
    public Task<PackageManifestDto?> GetPackageManifestAsync(string digest, CancellationToken ct = default) =>
        GetOrNullAsync<PackageManifestDto>($"/api/packages/{Uri.EscapeDataString(digest)}/manifest", ct);

    /// <summary>Null if the agent has never reported in at all (404) — a real, expected state for a freshly registered agent, not an error.</summary>
    public Task<AgentStatusSnapshotDto?> GetLatestReportAsync(string agentName, CancellationToken ct = default) =>
        GetOrNullAsync<AgentStatusSnapshotDto>($"/api/agents/{Uri.EscapeDataString(agentName)}/report/latest", ct);

    /// <summary>Fire-and-forget from the control plane's own point of view (see AgentCommandRequest) — a 202 here means "delivered to the hub," not "the service actually stopped." The dialog that calls this refreshes the status snapshot separately to show the real outcome.</summary>
    public Task SendCommandAsync(string agentName, AgentCommandRequest request, CancellationToken ct = default) =>
        SendAsync<object>(new HttpRequestMessage(HttpMethod.Post, $"/api/agents/{Uri.EscapeDataString(agentName)}/commands") { Content = JsonContent.Create(request) }, ct);

    public async Task<List<AgentLogEntryDto>> GetLogsAsync(string agentName, string applicationName, long sinceId = 0, int take = 200, CancellationToken ct = default)
    {
        var url = $"/api/agents/{Uri.EscapeDataString(agentName)}/logs?application={Uri.EscapeDataString(applicationName)}&sinceId={sinceId}&take={take}";
        return await SendAsync<List<AgentLogEntryDto>>(new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false) ?? [];
    }

    // ---- Access: keys and join tokens (Operator only, both here and on the control plane) ----

    public async Task<List<ApiKeyDto>> GetApiKeysAsync(CancellationToken ct = default) =>
        await SendAsync<List<ApiKeyDto>>(new HttpRequestMessage(HttpMethod.Get, "/api/api-keys"), ct).ConfigureAwait(false) ?? [];

    /// <summary>The response carries the key once; it is not retrievable afterwards.</summary>
    public Task<CreateApiKeyResponse> CreateApiKeyAsync(CreateApiKeyRequest request, CancellationToken ct = default) =>
        SendAsync<CreateApiKeyResponse>(new HttpRequestMessage(HttpMethod.Post, "/api/api-keys") { Content = JsonContent.Create(request) }, ct)!;

    public Task RevokeApiKeyAsync(string name, CancellationToken ct = default) =>
        SendAsync<object>(new HttpRequestMessage(HttpMethod.Delete, $"/api/api-keys/{Uri.EscapeDataString(name)}"), ct);

    public async Task<List<JoinTokenDto>> GetJoinTokensAsync(CancellationToken ct = default) =>
        await SendAsync<List<JoinTokenDto>>(new HttpRequestMessage(HttpMethod.Get, "/api/join-tokens"), ct).ConfigureAwait(false) ?? [];

    /// <summary>The response carries the token once; it is not retrievable afterwards.</summary>
    public Task<CreateJoinTokenResponse> CreateJoinTokenAsync(CreateJoinTokenRequest request, CancellationToken ct = default) =>
        SendAsync<CreateJoinTokenResponse>(new HttpRequestMessage(HttpMethod.Post, "/api/join-tokens") { Content = JsonContent.Create(request) }, ct)!;

    public Task RevokeJoinTokenAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<object>(new HttpRequestMessage(HttpMethod.Delete, $"/api/join-tokens/{id}"), ct);

    private async Task<T?> GetOrNullAsync<T>(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await PrepareAsync(request).ConfigureAwait(false);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return default;
        }

        await ThrowIfUnsuccessfulAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task<T?> SendAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        await PrepareAsync(request).ConfigureAwait(false);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await ThrowIfUnsuccessfulAsync(response, ct).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NoContent)
        {
            return default;
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>The bearer, the operator header, and - for anything but a GET - the Operator check. Refused here, before any bytes leave, so a Viewer's click never reaches the control plane under the portal's key.</summary>
    private async Task PrepareAsync(HttpRequestMessage request)
    {
        if (_credential?.Key is { } key)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        var user = await CurrentUserAsync().ConfigureAwait(false);
        if (user?.Identity?.Name is { Length: > 0 } name)
        {
            request.Headers.TryAddWithoutValidation(OperatorHeader, name);
        }

        if (request.Method != HttpMethod.Get && user is not null && _authorization is not null)
        {
            var verdict = await _authorization.AuthorizeAsync(user, PortalRoles.Operator).ConfigureAwait(false);
            if (!verdict.Succeeded)
            {
                throw new ApiException(HttpStatusCode.Forbidden,
                    "This needs the Operator role. Your Windows account is a Viewer here: every page, no changes. The portal refuses the change before it reaches the control plane.");
            }
        }
    }

    private async Task<ClaimsPrincipal?> CurrentUserAsync()
    {
        if (_authentication is null)
        {
            return null;
        }

        try
        {
            return (await _authentication.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
        }
        catch (InvalidOperationException)
        {
            // No authentication state in this context (a background call outside a circuit); the
            // request goes out with the key alone and no operator attribution.
            return null;
        }
    }

    private static async Task ThrowIfUnsuccessfulAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new ApiException(response.StatusCode,
                "The control plane refused the portal's own credential (401): ControlPlane:ApiKey is missing, revoked or expired. On the control plane host: " +
                "'Enlist.ControlPlane.exe create-api-key --name portal --role Operator --expires never', then store it with 'Enlist.Portal.exe protect <key>'.");
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new ApiException(response.StatusCode, string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase ?? "Request failed." : body);
    }
}

public sealed class ApiException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public ApiException(HttpStatusCode statusCode, string message) : base(message) => StatusCode = statusCode;
}

/// <summary>
/// Portal-local mirror of Enlist.Agent.Status.AgentStatusSnapshot, kept deliberately separate rather
/// than a shared reference — the control plane stores this as an opaque JSON blob specifically so the
/// agent's own status shape can evolve without a control-plane migration (see AgentReportEntity); the
/// portal choosing to interpret that JSON for display is a display-layer concern, not a reason to
/// couple the two assemblies together. A field this doesn't know about is just ignored by
/// System.Text.Json; a field it expects that's missing comes back as its default rather than a
/// deserialization failure, which is what JsonElement-free tolerance actually needs here.
/// </summary>
public sealed record AgentStatusSnapshotDto(DateTimeOffset GeneratedAtUtc, List<AgentApplicationStatusDto> Applications);

public sealed record AgentApplicationStatusDto(
    string Name,
    string State,
    int? Pid,
    int RestartCount,
    List<AgentServiceStatusDto> Services,
    List<AgentJobStatusDto> Jobs,
    string? Description = null,
    string? IsolationMode = null,
    string? RuntimeId = null,
    List<ResolvedEndpointDto>? Endpoints = null);

public sealed record AgentServiceStatusDto(string Name, string State, string? Description = null);

/// <summary>LastRunOutcome is "Succeeded", "Failed" or "Cancelled" (the agent's JobRunOutcome), or null when the job has not run since its agent started. Cancelled is its own value on purpose — a run an operator stopped is not a failure.</summary>
public sealed record AgentJobStatusDto(string Name, DateTimeOffset? LastRunUtc, string? LastRunOutcome, bool Paused = false, string? Cron = null, string? Description = null);
