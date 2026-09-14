using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Enlist.Installer.Detection
{
    /// <summary>Which container engine the Agent page should offer, and what to say beside it.</summary>
    public sealed class EngineResult
    {
        public EngineResult(string engine, bool available, string detail)
        {
            Engine = engine;
            Available = available;
            Detail = detail;
        }

        /// <summary>The value that becomes AGENT_ENGINE: "wslc" or "docker".</summary>
        public string Engine { get; }

        public bool Available { get; }

        /// <summary>A version when the engine answered, a reason when it did not.</summary>
        public string Detail { get; }

        public override string ToString() => Engine + ": " + (Available ? Detail : "unavailable (" + Detail + ")");
    }

    /// <summary>
    /// Detects wslc and docker the way the AGENT does, rather than the way it is tempting to.
    ///
    /// Being on PATH proves nothing. Both CLIs install separately from the engine behind them, and
    /// the failure that matters - the daemon or the WSL service being down - looks exactly like
    /// success to a file-existence check. So both are detected by ASKING THE SERVER for its version,
    /// which is the same thing DockerContainerEngine.ProbeAsync does and for the same reason recorded
    /// there: this machine had the Docker CLI present and the daemon stopped, mid-session.
    ///
    /// Detection is a snapshot and the design says so (section 4). An engine up at install time can be
    /// down at 3 a.m.; the agent re-probes on every heartbeat and reports what it finds. All the
    /// installer decides is which radio buttons to enable.
    /// </summary>
    public static class ContainerEngineDetection
    {
        /// <summary>
        /// Long enough for a cold daemon on a busy machine, short enough that a wizard page does not
        /// look hung. Both engines are probed in parallel, so this is the page's total wait, not the
        /// sum.
        /// </summary>
        public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

        /// <summary>Where WSL's installer puts wslc when it does not put it on PATH, which is most of the time.</summary>
        public static string WslcPath()
        {
            var installed = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WSL", "wslc.exe");
            return File.Exists(installed) ? installed : "wslc";
        }

        /// <summary>
        /// The engine the Agent page proposes: Docker when it answers, and otherwise none - never wslc,
        /// even when wslc is the only engine found.
        ///
        /// This used to be "the first engine that answers", probed wslc first, and that was wrong in a
        /// way only an installed agent could show. Detection runs as the OPERATOR, where wslc works; the
        /// agent runs as a Windows service under LocalSystem, where it does not - WSL belongs to a
        /// signed-in user, and `wslc list` run as SYSTEM hung for three minutes without a word
        /// (installer\live-e2e.ps1, 2026-09-14). Docker Desktop's engine grants SYSTEM access and the
        /// same run deployed a container through it. So on a machine with both, the old default was
        /// the one engine the service could not use.
        ///
        /// wslc is still reported on the Prerequisites page and can still be typed on the Agent page,
        /// which then says why it will not work for a service (InstallPlan.AgentEngineWarning).
        /// </summary>
        public static string? DefaultEngine(IEnumerable<EngineResult> engines) =>
            engines.Any(e => e.Available && string.Equals(e.Engine, "docker", StringComparison.OrdinalIgnoreCase)) ? "docker" : null;

        public static Task<EngineResult> ProbeWslcAsync()
        {
            // `wslc version` prints its own version. An exit code alone would not distinguish "the CLI
            // is there" from "WSL is running", which is the distinction that matters.
            return ProbeAsync("wslc", WslcPath(), new[] { "version" });
        }

        public static Task<EngineResult> ProbeDockerAsync()
        {
            // --format {{.Server.Version}} asks the DAEMON, not the client. A client-only answer is
            // the exact false positive this avoids.
            return ProbeAsync("docker", "docker", new[] { "version", "--format", "{{.Server.Version}}" });
        }

        private static async Task<EngineResult> ProbeAsync(string engine, string executable, string[] arguments)
        {
            try
            {
                var startInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                foreach (var argument in arguments)
                {
                    startInfo.Arguments += (startInfo.Arguments.Length == 0 ? "" : " ") + Quote(argument);
                }

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        return new EngineResult(engine, false, "could not be started");
                    }

                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var exited = await Task.Run(() => process.WaitForExit((int)ProbeTimeout.TotalMilliseconds))
                        .ConfigureAwait(false);

                    if (!exited)
                    {
                        // A CLI that never answers is as unusable as one that is absent, and leaving it
                        // running would outlive the wizard page that asked.
                        TryKill(process);
                        return new EngineResult(engine, false, "did not answer within " + (int)ProbeTimeout.TotalSeconds + "s");
                    }

                    if (process.ExitCode != 0)
                    {
                        return new EngineResult(engine, false, "exited " + process.ExitCode + ", so its engine is not running");
                    }

                    var version = FirstLine(await stdout.ConfigureAwait(false));
                    return new EngineResult(
                        engine,
                        true,
                        version.Length == 0 ? "running" : version);
                }
            }
            catch (Exception ex)
            {
                // Process.Start throws rather than exiting non-zero when the executable is absent, so
                // this is the ordinary "not installed" path and not an exceptional one.
                return new EngineResult(engine, false, ex.Message);
            }
        }

        private static string Quote(string argument) =>
            argument.IndexOf(' ') < 0 ? argument : "\"" + argument + "\"";

        private static string FirstLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "";
            }

            // wslc prints several lines; the first is the one naming its own version.
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? "" : lines[0].Trim();
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
                // Already gone, or not ours to kill. The timeout is the report that matters.
            }
        }
    }
}
