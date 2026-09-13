using Enlist.Runner.Discovery;

namespace Enlist.Runner.Execution;

internal sealed class RunningServiceState
{
    public required DiscoveredService Service { get; init; }
    public required object Instance { get; init; }
    public required CancellationTokenSource StoppingCts { get; init; }
}
