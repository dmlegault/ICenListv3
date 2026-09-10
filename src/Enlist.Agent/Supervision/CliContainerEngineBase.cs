using System.Diagnostics;

namespace Enlist.Agent.Supervision;

/// <summary>
/// What every CLI-driven engine shares: running one command with its output captured, bounded in
/// time, and killed on expiry. <see cref="DockerContainerEngine"/> and <see cref="WslContainerEngine"/>
/// are argument builders over this — the same timeout, the same failure shape, the same test (a
/// stand-in executable that never answers) proving both.
/// </summary>
public abstract class CliContainerEngineBase
{
    protected string Executable { get; }

    protected TimeSpan CommandTimeout { get; }

    /// <param name="commandTimeout">How long any single command other than a deliberately long-lived one may take before it is killed and reported. A daemon that accepts the connection and never answers used to block the agent's reconcile loop indefinitely.</param>
    protected CliContainerEngineBase(string executable, TimeSpan? commandTimeout)
    {
        Executable = executable;
        CommandTimeout = commandTimeout ?? TimeSpan.FromSeconds(60);
    }

    protected async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{Executable}'. Is a container engine installed and on PATH?");

        // Bounded, or the caller is at the daemon's mercy: every backend call is short by nature, and
        // one that is not means the engine has stopped answering. On expiry the CLI is killed — it is
        // the wait that is abandoned, never the container — and the caller gets a TimeoutException
        // that names the command, which the crash-restart and probe paths already know how to report.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? CommandTimeout);

        // Both streams are read concurrently with the wait. Reading one to completion first can
        // deadlock on a command that fills the other pipe's buffer.
        var stdout = process.StandardOutput.ReadToEndAsync(limit.Token);
        var stderr = process.StandardError.ReadToEndAsync(limit.Token);

        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            KillQuietly(process);
            throw new TimeoutException(
                $"'{Executable} {args[0]}' did not finish within {(timeout ?? CommandTimeout).TotalSeconds:0}s - the container engine is not answering. " +
                "Check that the engine is running and responsive.");
        }

        return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static void KillQuietly(Process process)
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
            // Already gone, or not ours to kill — either way the timeout is the report that matters.
        }
    }
}
