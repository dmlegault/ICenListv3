using Enlist.ControlPlane.Contracts;

using Microsoft.AspNetCore.SignalR;

namespace Enlist.ControlPlane.Hubs;

/// <summary>
/// Agents dial out to this (design doc section 6: "Connection direction: agents dial out" — no
/// inbound firewall rules needed on application servers). Each connection joins a group named after
/// its own agent, so a change to one agent's policies only notifies that agent, not every agent in
/// the fleet. The push carries no payload — it's purely "something changed, go re-fetch" — so the
/// REST policy contract stays the single source of truth for what a policy rule actually contains,
/// and the two can never drift against each other.
/// </summary>
public sealed class ApplicationPolicyHub : Hub
{
    public async Task JoinAgentGroup(string agentName)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(agentName));
    }

    public static string GroupName(string agentName) => $"agent:{agentName}";
}

public static class ApplicationPolicyHubNotifier
{
    public static Task NotifyChangedAsync(IHubContext<ApplicationPolicyHub> hub, string agentName) =>
        hub.Clients.Group(ApplicationPolicyHub.GroupName(agentName)).SendAsync(ApplicationPolicyHubContract.ApplicationPoliciesChangedMethod);

    /// <summary>Fire-and-forget from the control plane's own point of view — no ack is awaited here. The command's actual effect shows up via the agent's next status snapshot, the same eventually-consistent pattern every other push in this system already uses.</summary>
    public static Task SendCommandAsync(IHubContext<ApplicationPolicyHub> hub, string agentName, AgentCommandRequest command) =>
        hub.Clients.Group(ApplicationPolicyHub.GroupName(agentName)).SendAsync(ApplicationPolicyHubContract.ExecuteCommandMethod, command);
}
