using System.Net.Http.Json;

using Enlist.Agent.Logging;
using Enlist.ControlPlane.Contracts;

namespace Enlist.Agent.Status;

/// <summary>
/// POSTs each snapshot to the control plane. Best-effort — a failed report must never take the agent
/// down, same posture as AgentFileLogSink's own writes — but not silent: the first failure, one line
/// per five minutes while it lasts, and the recovery go to the agent log through
/// <see cref="IReportsDiagnostics"/>. A control plane answering 500 to every report for an hour used
/// to be indistinguishable from one that was fine.
/// </summary>
public sealed class ControlPlaneStatusReporter : IStatusReporter, IReportsDiagnostics
{
    private static readonly TimeSpan NoticeWindow = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly string _agentName;
    private readonly FailureNotice _statusNotice = new("Status report to the control plane", NoticeWindow);
    private readonly FailureNotice _capabilitiesNotice = new("Capability report to the control plane", NoticeWindow);

    public event Action<string>? Diagnostic;

    public ControlPlaneStatusReporter(HttpClient http, string agentName)
    {
        _http = http;
        _agentName = agentName;
    }

    public Task ReportCapabilitiesAsync(AgentCapabilitiesDto capabilities, CancellationToken ct) =>
        SendAsync(_capabilitiesNotice, ct, () => _http.PutAsJsonAsync(
            $"/api/agents/{Uri.EscapeDataString(_agentName)}/capabilities", new SetAgentCapabilitiesRequest(capabilities), ct));

    public Task ReportAsync(AgentStatusSnapshot snapshot, CancellationToken ct) =>
        SendAsync(_statusNotice, ct, () => _http.PostAsJsonAsync(
            $"/api/agents/{Uri.EscapeDataString(_agentName)}/report", snapshot, ct));

    private async Task SendAsync(FailureNotice notice, CancellationToken ct, Func<Task<HttpResponseMessage>> send)
    {
        try
        {
            using var response = await send().ConfigureAwait(false);

            // A 500 is a failure too. Previously only an exception counted, so a control plane that was
            // up but refusing every report looked exactly like one that was accepting them.
            response.EnsureSuccessStatusCode();

            if (notice.Succeeded() is { } recovered)
            {
                Diagnostic?.Invoke(recovered);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down — not a failure of the control plane.
        }
        catch (Exception ex)
        {
            if (notice.Failed(TransportFailure.Describe(ex)) is { } line)
            {
                Diagnostic?.Invoke(line);
            }
        }
    }
}
