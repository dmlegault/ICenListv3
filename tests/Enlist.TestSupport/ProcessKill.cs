using System.Diagnostics;

namespace Enlist.TestSupport;

/// <summary>
/// Kill a spawned test process and say nothing if it is already gone.
///
/// The mirror of the agent's own Enlist.Agent.Supervision.ProcessKill, kept separate because
/// Enlist.TestSupport deliberately does not reference Enlist.Agent - the test servers here are
/// started by tests that have no business depending on the agent. Cleanup is best-effort by
/// definition: a test that already failed must not then fail differently because the teardown raced
/// the process exiting on its own.
///
/// entireProcessTree matters here. A test server spawns children (dotnet's own host, and under the
/// control plane a LocalDB instance if nothing pinned it first - see LocalDb.EnsureStartedAsync for
/// why that pinning happens before any child is started).
/// </summary>
internal static class ProcessKill
{
    public static void Quietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // See the class summary: nothing to do, and nothing worth saying.
        }
    }
}
