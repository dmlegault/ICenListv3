namespace Enlist.Agent.Configuration;

/// <summary>
/// Optional capability an IAssignmentSource can implement if it manages a local package cache that
/// needs pruning — AgentHost calls this only AFTER ReconcileAsync completes, never before, because
/// pruning a digest's files while the process that was using them hasn't been stopped yet loses a
/// real race against the OS's file lock (see ControlPlaneAssignmentSource for exactly how this failed
/// before it was split out this way).
/// </summary>
public interface IPrunesPackageCache
{
    Task PruneUnusedPackagesAsync(CancellationToken ct);
}
