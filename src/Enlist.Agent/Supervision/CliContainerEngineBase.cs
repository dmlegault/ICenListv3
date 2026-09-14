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

    /// <summary>
    /// The pull policy every `run` passes: never. Both engines default to "missing", which fetches an
    /// image that is not already local from the engine's default registry - Docker Hub, for a name
    /// like enlist/runner:3.0.0 - and runs whatever it finds there. That namespace is not ours. An
    /// agent running as LocalSystem would then start somebody else's image with an application
    /// package mounted into it, and the first sign would be a container that behaved oddly.
    ///
    /// The image an agent runs is put on the machine deliberately - built, or loaded from the
    /// installer - so a missing one is an installation problem to report, not something to go and
    /// find on the internet.
    /// </summary>
    protected const string PullPolicy = "never";

    /// <summary>
    /// The error for a run refused because the image is not on this machine, saying what the operator
    /// can do about it. The enList runner image is handed out as a download beside the installer,
    /// enlist-runner-&lt;version&gt;.tar, and the way to give it to an agent is its Images folder: the
    /// agent loads it from there itself, into the store of the account it runs as - which matters,
    /// because wslc's store is per account and an image loaded in an operator's own shell is not one a
    /// LocalSystem agent can see. See RunnerImageDropFolder.
    /// </summary>
    protected static ContainerImageMissingException MissingImage(string engine, string image, string detail) =>
        new($"{engine} run failed: the image '{image}' is not on this machine ({detail}). " +
            $"The agent never pulls images. Put the enList runner image download (enlist-runner-<version>.tar) in the Images folder " +
            $"under the agent's data directory - the agent loads it into {engine} itself - or point the application at an image that is present.");

    /// <summary>
    /// How long loading an image archive may take. Minutes, not the ordinary command budget: an 80 MB
    /// runner image loads in a couple of seconds, but a larger one onto a busy disk can take much longer,
    /// and a load cut short would be reported as a failure that was really just slow.
    /// </summary>
    protected static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(10);

    /// <summary>`load -i &lt;archive&gt;`, which both engines spell the same way.</summary>
    protected async Task LoadArchiveAsync(string engine, string archivePath, CancellationToken ct)
    {
        var result = await RunCliAsync(["load", "-i", archivePath], ct, LoadTimeout).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var reason = result.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
                ?? $"exit code {result.ExitCode}";
            throw new InvalidOperationException($"{engine} load failed ({result.ExitCode}): {reason}");
        }
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
            throw new TimeoutException(
                $"'{Executable} {args[0]}' did not finish within {(timeout ?? CommandTimeout).TotalSeconds:0}s - the container engine is not answering. " +
                "Check that the engine is running and responsive.");
        }
        finally
        {
            // In the finally, not only on the timeout path. The CALLER's token cancels on shutdown,
            // and that threw straight out of here with the CLI still running - so a stop that raced
            // the agent's own shutdown left a `docker wait` or a `wslc inspect` behind, owned by
            // nobody, for as long as the engine took to answer. The timeout path killed it; the
            // cancellation path, which is the common one, did not.
            //
            // Killing a CLI is always safe: it is a client. The container it was asking about is
            // unaffected, which is what makes this the right thing to do on every abandoned wait.
            ProcessKill.Quietly(process);
        }

        return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }
}
