using Enlist.Runner.Legacy.Discovery;

namespace Enlist.Runner.Legacy.Execution;

internal sealed class RunningServiceState
{
    public required DiscoveredService Service { get; init; }
    public required object Instance { get; init; }
    public required CancellationTokenSource StoppingCts { get; init; }

    /// <summary>The (possibly still-running, per the design's async Start allowance) task returned by invoking Start.</summary>
    public Task? StartTask { get; set; }
}
