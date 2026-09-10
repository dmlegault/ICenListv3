namespace Enlist.Agent.Configuration;

/// <summary>Wraps a fixed AgentAssignments that never changes — what AgentHost's original (still-supported) constructor uses internally, so every existing local-file/in-memory call site keeps compiling and behaving exactly as it did before IAssignmentSource existed.</summary>
public sealed class StaticAssignmentSource : IAssignmentSource
{
    private readonly AgentAssignments _assignments;

    public StaticAssignmentSource(AgentAssignments assignments) => _assignments = assignments;

    public Task<AgentAssignments> GetCurrentAsync(CancellationToken ct) => Task.FromResult(_assignments);

    public Task WaitForChangeAsync(CancellationToken ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct);
}
