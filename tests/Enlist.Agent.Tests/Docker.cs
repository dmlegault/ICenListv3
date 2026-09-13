using System.Diagnostics;

namespace Enlist.Agent.Tests;

/// <summary>
/// The `docker` CLI, as the container tests use it.
///
/// These tests assert on what the ENGINE actually did - is the container still there, what did it
/// publish, what image is it running - rather than on what the agent believes it asked for. Reading
/// that back means shelling out to the same CLI the agent drives, and four test classes had grown
/// their own copy of the same twenty lines to do it.
///
/// Everything here swallows failure and reports it as false or empty. That is right for this file
/// and wrong almost anywhere else: the first call every container test makes is the Skip check, and
/// "docker is not installed" has to arrive as a skip rather than as an exception from inside a
/// helper. A test that gets past the Skip and then hits a broken engine fails on its own assertion,
/// with its own message, which is the one worth reading.
/// </summary>
internal static class Docker
{
    /// <summary>Built by docs/05-operations/Container-Developer-Guide.md; absent on a machine that has never built it, which is why every container test starts with a Skip.</summary>
    public const string RunnerImage = "enlist/runner:dev";

    /// <summary>Generous: a cold `docker image inspect` on a busy machine is slow, and a hung CLI must not hang the whole test run.</summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>True if the command exited 0. False if it did not, if docker is not installed, or if it hung.</summary>
    public static async Task<bool> SucceedsAsync(params string[] args)
    {
        try
        {
            using var process = Start(args);
            if (process is null)
            {
                return false;
            }

            using var cts = new CancellationTokenSource(CommandTimeout);
            await process.WaitForExitAsync(cts.Token);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The command's stdout, or empty if it could not be run at all.</summary>
    public static async Task<string> CaptureAsync(params string[] args)
    {
        try
        {
            using var process = Start(args);
            if (process is null)
            {
                return "";
            }

            // Read BEFORE waiting: a command that fills the pipe buffer deadlocks against its own
            // WaitForExit otherwise, and `docker inspect` output is not small.
            var stdout = await process.StandardOutput.ReadToEndAsync();
            using var cts = new CancellationTokenSource(CommandTimeout);
            await process.WaitForExitAsync(cts.Token);
            return stdout;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>The Skip condition every container test opens with: it proves the engine answers AND the image is built.</summary>
    public static Task<bool> ImageAvailableAsync(string image = RunnerImage) =>
        SucceedsAsync("image", "inspect", image);

    /// <summary>Terminating a container without warning - no shutdown command, no cooperative anything.</summary>
    public static Task<bool> KillAsync(string containerId) => SucceedsAsync("kill", containerId);

    public static Task<bool> ContainerExistsAsync(string containerId) =>
        SucceedsAsync("inspect", "--format", "{{.Id}}", containerId);

    private static Process? Start(string[] args)
    {
        var psi = new ProcessStartInfo("docker")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        return Process.Start(psi);
    }
}
