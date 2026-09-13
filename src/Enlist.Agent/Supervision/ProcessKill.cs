using System.Diagnostics;

namespace Enlist.Agent.Supervision;

/// <summary>
/// Kill a child process the agent started, and say nothing if it is already gone.
///
/// Every caller here is in a path that is already finishing: a stop whose grace period expired, a
/// dispose, a start that failed, a container CLI abandoned because the wait was cancelled. In all of
/// them the process racing to exit on its own is a normal outcome, not a fault to report - and the
/// caller has something more important to report anyway (the timeout, the start failure). So the
/// exception is swallowed rather than logged: a line saying "could not kill a process that had
/// already exited" is noise on top of the message the operator actually needs.
///
/// entireProcessTree because a runner or a container CLI may have started children of its own, and
/// the orphan left behind by killing only the parent is exactly what the agent is trying to prevent.
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
            // See the class summary: already gone, or not ours to kill. Either way there is nothing
            // to do about it and nothing worth saying.
        }
    }
}
