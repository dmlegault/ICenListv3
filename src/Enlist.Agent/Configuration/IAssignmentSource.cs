namespace Enlist.Agent.Configuration;

/// <summary>
/// Where AgentHost gets its desired state from — a local file or the control plane (REST
/// + SignalR). AgentHost itself doesn't know or care which; it calls GetCurrentAsync once at startup and
/// again every time WaitForChangeAsync completes, and reconciles against whatever comes back.
/// </summary>
public interface IAssignmentSource
{
    Task<AgentAssignments> GetCurrentAsync(CancellationToken ct);

    /// <summary>
    /// Completes when the assignments MIGHT have changed and are worth re-fetching. A source with no
    /// way to detect changes (the local file) can simply never complete this until cancelled — that
    /// reproduces "read once at startup" exactly, which is why it is still the default
    /// for AgentHost's local-file constructor overload rather than a regression.
    /// </summary>
    Task WaitForChangeAsync(CancellationToken ct);
}
