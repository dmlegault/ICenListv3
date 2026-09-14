using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Enlist.Installer.Detection;

namespace Enlist.Installer.Ba
{
    /// <summary>What happened to one step, in words the Finish page can show.</summary>
    public sealed class PostInstallResult
    {
        public PostInstallResult(PostInstallStepKind kind, bool succeeded, bool optional, string message)
        {
            Kind = kind;
            Succeeded = succeeded;
            Optional = optional;
            Message = message;
        }

        public PostInstallStepKind Kind { get; }

        public bool Succeeded { get; }

        public bool Optional { get; }

        /// <summary>Never contains a secret. See <see cref="PostInstallRunner"/>.</summary>
        public string Message { get; }
    }

    /// <summary>
    /// Runs the steps <see cref="PostInstall"/> describes: the schema, the portal's key, the agent's
    /// enrollment. Everything the MSIs deliberately will not do.
    ///
    /// THE SECRETS ARE ADDED HERE AND NOWHERE ELSE. A step carries no join token, no API key and no
    /// connection string, because a step gets described in a progress message and written to a log.
    /// This class holds them for as long as one process invocation takes and never puts them anywhere
    /// they persist:
    ///
    ///   - the connection string goes in the child's ENVIRONMENT, not its command line, because a
    ///     command line is readable out of the process list by any local user and a SQL password may
    ///     be in it;
    ///   - the join token goes on the enroll verb's command line, which is the one place it has to
    ///     go, for the lifetime of a process that exits in under a second - as against the service
    ///     command line, where `sc qc` would show it to anyone, for ever;
    ///   - the portal's key is passed to `protect` the same way and is never logged, never shown and
    ///     never written by this process - Enlist.Portal.exe writes it, protected, itself.
    ///
    /// Output is captured but only ever reported on FAILURE, and even then the trailing lines rather
    /// than everything, because create-api-key's successful output is the key.
    /// </summary>
    public sealed class PostInstallRunner
    {
        private readonly InstallPlan _plan;
        private readonly IReadOnlyCollection<string> _executedPackages;
        private readonly Action<string> _progress;
        private readonly Action<string> _log;

        /// <param name="executedPackages">
        /// The Burn package IDs actually installed, modified, repaired or upgraded in this apply. See
        /// PostInstall.Steps for why the plan on its own is not enough - running from the plan alone
        /// re-keyed a portal that the apply had not touched.
        /// </param>
        public PostInstallRunner(InstallPlan plan, IReadOnlyCollection<string> executedPackages, Action<string> progress, Action<string> log)
        {
            _plan = plan;
            _executedPackages = executedPackages;
            _progress = progress;
            _log = log;
        }

        public async Task<IReadOnlyList<PostInstallResult>> RunAsync()
        {
            var results = new List<PostInstallResult>();
            string? portalKey = null;

            _log("packages executed this apply: " + (_executedPackages.Count == 0 ? "(none)" : string.Join(", ", _executedPackages.ToArray())));

            foreach (var step in PostInstall.Steps(_plan, _executedPackages))
            {
                _progress(step.Description);
                _log($"post-install: {step.Description} ({step.Executable} {string.Join(" ", step.Arguments)})");

                if (!System.IO.File.Exists(step.Executable))
                {
                    // A missing executable means the package that carries it is not where the plan
                    // says. Worth its own message: "the file is not there" is a different problem from
                    // "the command failed".
                    results.Add(Failed(step, $"{step.Executable} was not found."));
                    if (!step.Optional) { break; }
                    continue;
                }

                var arguments = new List<string>(step.Arguments);

                // The secret, appended at the last possible moment.
                switch (step.Kind)
                {
                    case PostInstallStepKind.StorePortalKey:
                        if (string.IsNullOrEmpty(portalKey))
                        {
                            results.Add(Failed(step, "No key was produced by the previous step."));
                            break;
                        }

                        arguments.Add(portalKey!);
                        arguments.Add("--store");
                        arguments.Add("--service-account");
                        arguments.Add(_plan.PortalAccount);
                        break;

                    case PostInstallStepKind.EnrollAgent:
                        arguments.Add("--join-token");
                        arguments.Add(_plan.AgentJoinToken);
                        break;
                }

                if (step.Kind == PostInstallStepKind.StorePortalKey && string.IsNullOrEmpty(portalKey))
                {
                    break;
                }

                var (exitCode, output) = await RunAsync(step, arguments).ConfigureAwait(false);

                if (exitCode != 0)
                {
                    results.Add(Failed(step, Summarise(output, exitCode)));
                    if (!step.Optional) { break; }
                    continue;
                }

                if (step.Kind == PostInstallStepKind.CreatePortalKey)
                {
                    portalKey = PostInstall.ParseApiKey(output);
                    if (string.IsNullOrEmpty(portalKey))
                    {
                        // Exit 0 and no key is a contract change, not a runtime failure, and saying
                        // "it worked" here would leave a portal that 401s with nothing to explain it.
                        results.Add(Failed(step, "The control plane reported success but printed no key."));
                        break;
                    }
                }

                results.Add(new PostInstallResult(step.Kind, true, step.Optional, step.Description + " - done."));
            }

            // Held no longer than it takes to hand it to the portal.
            portalKey = null;
            return results;
        }

        private async Task<(int ExitCode, string Output)> RunAsync(PostInstallStep step, IReadOnlyList<string> arguments)
        {
            var info = new ProcessStartInfo(step.Executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = System.IO.Path.GetDirectoryName(step.Executable),
            };

            foreach (var argument in arguments)
            {
                info.Arguments += (info.Arguments.Length == 0 ? "" : " ") + Quote(argument);
            }

            if (step.NeedsDatabase)
            {
                // The double underscore is configuration's nesting separator: this is exactly
                // ConnectionStrings:ControlPlane, which is what ManagementCli reads.
                info.EnvironmentVariables["ConnectionStrings__ControlPlane"] = Probes.BuildConnectionString(
                    _plan.DatabaseServer,
                    _plan.DatabaseName,
                    _plan.DatabaseWindowsAuthentication,
                    _plan.DatabaseUser,
                    _plan.DatabasePassword);
            }

            var captured = new StringBuilder();

            using (var process = Process.Start(info))
            {
                if (process == null)
                {
                    return (-1, "The process could not be started.");
                }

                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();

                // Generous, because apply-schema on a cold LocalDB instance includes starting the
                // instance, and a timeout here would report a failure for something that was working.
                var exited = await Task.Run(() => process.WaitForExit((int)TimeSpan.FromMinutes(5).TotalMilliseconds))
                    .ConfigureAwait(false);

                if (!exited)
                {
                    try { process.Kill(); } catch { }
                    return (-1, "It did not finish within five minutes.");
                }

                captured.Append(await stdout.ConfigureAwait(false));
                captured.Append(await stderr.ConfigureAwait(false));
                return (process.ExitCode, captured.ToString());
            }
        }

        private PostInstallResult Failed(PostInstallStep step, string message) =>
            new PostInstallResult(step.Kind, false, step.Optional, step.Description + " - " + message);

        /// <summary>
        /// The last few lines of a failure, which is where these executables put the reason.
        ///
        /// Trimmed rather than passed through whole because the caller shows this on a page, and
        /// because the successful output of one of these steps IS a secret - reporting everything by
        /// habit is how it would eventually be reported on a step that partly succeeded.
        /// </summary>
        private static string Summarise(string output, int exitCode)
        {
            var lines = (output ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
            {
                return $"exited {exitCode} with no output.";
            }

            var from = Math.Max(0, lines.Length - 3);
            return string.Join(" ", lines, from, lines.Length - from).Trim();
        }

        private static string Quote(string argument) =>
            argument.IndexOf(' ') < 0 && argument.Length > 0 ? argument : "\"" + argument.TrimEnd('\\') + "\"";
    }
}
