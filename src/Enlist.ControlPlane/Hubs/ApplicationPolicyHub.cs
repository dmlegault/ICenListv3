using Enlist.ControlPlane.Authentication;
using Enlist.ControlPlane.Contracts;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

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
    private readonly AuthenticationOptions _authentication;

    public ApplicationPolicyHub(IOptions<AuthenticationOptions> authentication)
    {
        _authentication = authentication.Value;
    }

    public async Task JoinAgentGroup(string agentName)
    {
        // An agent may join its own group and no other (Authentication-Design.md section 4.5).
        // Checked here rather than at connect time because the name arrives with this call, not with
        // the connection. Under Off — loopback only, by the startup rules — there is no credential to
        // compare against, and the group is as open as every other endpoint.
        if (_authentication.IsRequired)
        {
            var enrolledAs = Context.User?.FindFirst(EnlistClaims.Agent)?.Value;
            if (!string.Equals(enrolledAs, agentName, StringComparison.OrdinalIgnoreCase))
            {
                throw new HubException($"This connection is enrolled as '{enrolledAs}' and cannot join the group for '{agentName}'.");
            }
        }

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
