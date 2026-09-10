using Microsoft.Extensions.Hosting;

namespace Enlist.Agent;

/// <summary>Thin IHostedService adapter so the generic host (and, through it, the Windows SCM) drives AgentHost's own StartAsync/StopAsync — no logic of its own.</summary>
internal sealed class AgentBackgroundService : IHostedService
{
    private readonly AgentHost _agentHost;

    public AgentBackgroundService(AgentHost agentHost) => _agentHost = agentHost;

    public Task StartAsync(CancellationToken cancellationToken) => _agentHost.StartAsync();

    public Task StopAsync(CancellationToken cancellationToken) => _agentHost.StopAsync();
}
