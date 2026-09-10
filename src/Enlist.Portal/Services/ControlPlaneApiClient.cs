using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Enlist.ControlPlane.Contracts;

namespace Enlist.Portal.Services;

/// <summary>
/// The only thing the portal talks to (design doc section 8: "The portal never talks to an agent").
/// Thin wrapper over Enlist.ControlPlane's REST API — no business logic of its own, just typed calls
/// plus turning a non-success response into an ApiException pages can catch and show via Snackbar.
/// </summary>
public sealed class ControlPlaneApiClient
{
    private readonly HttpClient _http;

    public ControlPlaneApiClient(HttpClient http) => _http = http;

    public async Task<List<AgentDto>> GetAgentsAsync(CancellationToken ct = default) =>
        await SendAsync<List<AgentDto>>(new HttpRequestMessage(HttpMethod.Get, "/api/agents"), ct).ConfigureAwait(false) ?? [];

    public Task<AgentDto> SetAgentTagsAsync(string agentName, IReadOnlyDictionary<string, string> tags, CancellationToken ct = default) =>
        SendAsync<AgentDto>(new HttpRequestMessage(HttpMethod.Put, $"/api/agents/{Uri.EscapeDataString(agentName)}/tags") { Content = JsonContent.Create(new SetAgentTagsRequest(tags)) }, ct)!;

    /// <summary>Never blocked: a rule targets tags, not an agent, so nothing references the row — it simply reappears on the agent's next contact. (The 409 that once protected explicit placements went with them.)</summary>
    public Task DeleteAgentAsync(string agentName, CancellationToken ct = default) =>
        SendAsync<object>(new HttpRequestMessage(HttpMethod.Delete, $"/api/agents/{Uri.EscapeDataString(agentName)}"), ct);

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
    public async Task<PackageManifestDto?> GetPackageManifestAsync(string digest, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/packages/{Uri.EscapeDataString(digest)}/manifest", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await ThrowIfUnsuccessfulAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<PackageManifestDto>(cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Null if the agent has never reported in at all (404) — a real, expected state for a freshly registered agent, not an error.</summary>
    public async Task<AgentStatusSnapshotDto?> GetLatestReportAsync(string agentName, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/agents/{Uri.EscapeDataString(agentName)}/report/latest", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await ThrowIfUnsuccessfulAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<AgentStatusSnapshotDto>(cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Fire-and-forget from the control plane's own point of view (see AgentCommandRequest) — a 202 here means "delivered to the hub," not "the service actually stopped." The dialog that calls this refreshes the status snapshot separately to show the real outcome.</summary>
    public Task SendCommandAsync(string agentName, AgentCommandRequest request, CancellationToken ct = default) =>
        SendAsync<object>(new HttpRequestMessage(HttpMethod.Post, $"/api/agents/{Uri.EscapeDataString(agentName)}/commands") { Content = JsonContent.Create(request) }, ct);

    public async Task<List<AgentLogEntryDto>> GetLogsAsync(string agentName, string applicationName, long sinceId = 0, int take = 200, CancellationToken ct = default)
    {
        var url = $"/api/agents/{Uri.EscapeDataString(agentName)}/logs?application={Uri.EscapeDataString(applicationName)}&sinceId={sinceId}&take={take}";
        return await SendAsync<List<AgentLogEntryDto>>(new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false) ?? [];
    }

    private async Task<T?> SendAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await ThrowIfUnsuccessfulAsync(response, ct).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NoContent)
        {
            return default;
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
    }

    private static async Task ThrowIfUnsuccessfulAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
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
