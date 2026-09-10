using Enlist.Agent.Logging;

namespace Enlist.Agent.Configuration;

/// <summary>
/// Optional capability of an IAssignmentSource that learns about changes over a live connection:
/// whether that connection is delivering right now. AgentHost puts the state into the capabilities it
/// reports (AgentCapabilitiesDto.PushChannel) so the portal can tell "online" apart from "online and
/// receiving" — heartbeats travel over HTTP and keep flowing while the push connection is down, so from
/// outside the two are otherwise indistinguishable. Its Diagnostic lines (lost, reconnecting, restored) reach the agent
/// log the way every best-effort component's do — see IReportsDiagnostics.
/// </summary>
public interface IReportsPushChannel : IReportsDiagnostics
{
    /// <summary>One of PushChannelStates.</summary>
    string PushChannelState { get; }

    event Action? PushChannelStateChanged;
}
