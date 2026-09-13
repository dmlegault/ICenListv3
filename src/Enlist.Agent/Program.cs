using Enlist.Agent.Configuration;
using Enlist.Agent.Credentials;
using Enlist.Agent.Logging;
using Enlist.Agent.Status;
using Enlist.Agent.Supervision;
using Enlist.ControlPlane.Contracts;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Enlist.Agent;

/// <summary>
/// Two modes, sharing everything except where assignments come from:
///
///   enlist-agent --assignments &lt;path&gt; --runner-bin &lt;path&gt; [--legacy-runner-bin &lt;path&gt;] [--data &lt;path&gt;]
///     Local-only — assignments come from a file on disk, read once at startup.
///
///   enlist-agent --control-plane &lt;url&gt; --agent &lt;name&gt; --runner-bin &lt;path&gt; [--legacy-runner-bin &lt;path&gt;] [--data &lt;path&gt;]
///     Control-plane mode — assignments come from Enlist.ControlPlane's resolved policy rules, and
///     the agent reconciles live as they change (design doc section 6: dials OUT, never accepts
///     inbound connections). --agent defaults to Environment.MachineName if omitted.
///
/// --runner-bin points at the canonical enlist-runner build (the one folder containing
/// enlist-runner.exe/.dll/.deps.json/.runtimeconfig.json) that RunnerStaging copies from per
/// application instance — registered under RuntimeFlavors.Default (net10.0).
///
/// --legacy-runner-bin is optional and registers a second runner build under
/// RuntimeFlavors.NetFramework472 — an assignment whose ApplicationAssignment.RuntimeFlavor is "net472"
/// is staged from this path instead. Omit it and this agent simply can't start a net472-flavored
/// assignment (AgentHost logs why and marks it Failed rather than guessing).
///
/// --join-token &lt;token&gt; is for the FIRST start against a control plane that requires a credential
/// (Authentication-Design section 4): the agent exchanges it for its own long-lived agent token, stores
/// that DPAPI-protected under --data, and never needs the option again. Left in a service definition it
/// is ignored with a warning, because `sc qc` shows a service's arguments to anyone who can query it.
/// Against a control plane whose authentication is Off (loopback only - the demo) no credential is
/// needed and none is asked for.
///
/// Runs as a proper Windows Service when installed as one (sc.exe create / New-Service) — the SCM's
/// start/stop signals are routed to AgentHost.StartAsync/StopAsync through AgentBackgroundService.
/// Run directly in a terminal (no service context detected), it behaves like a normal console app:
/// Ctrl+C triggers the same graceful StopAsync via the generic host's own lifetime handling.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? assignmentsPath = null;
        string? controlPlaneUrl = null;
        string? agentName = null;
        string? runnerBinDirectory = null;
        string? legacyRunnerBinDirectory = null;
        var dataRoot = Path.Combine(AppContext.BaseDirectory, "Data");

        string? containerImage = null;
        string? containerEngineName = null;
        string? joinToken = null;

        // `i < args.Length`, not `args.Length - 1`, so a flag in the LAST position is seen rather than
        // skipped, and an unknown flag is refused rather than ignored. Both used to pass silently:
        // `--data` with no value simply did nothing, and a typo like `--runnerbin` left the real
        // setting unset, which surfaced pages later as a usage message naming a flag the operator
        // believed they had passed.
        for (var i = 0; i < args.Length; i++)
        {
            var flag = args[i];
            if (!flag.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"enlist-agent: unexpected argument '{flag}'.");
                return 2;
            }

            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine($"enlist-agent: '{flag}' needs a value.");
                return 2;
            }

            switch (flag)
            {
                case "--assignments":
                    assignmentsPath = args[i + 1];
                    break;
                case "--control-plane":
                    controlPlaneUrl = args[i + 1];
                    break;
                case "--agent":
                    agentName = args[i + 1];
                    break;
                case "--join-token":
                    joinToken = args[i + 1];
                    break;
                case "--runner-bin":
                    runnerBinDirectory = args[i + 1];
                    break;
                case "--legacy-runner-bin":
                    legacyRunnerBinDirectory = args[i + 1];
                    break;
                case "--data":
                    dataRoot = args[i + 1];
                    break;
                case "--container-image":
                    containerImage = args[i + 1];
                    break;
                case "--container-engine":
                    containerEngineName = args[i + 1];
                    break;
                default:
                    Console.Error.WriteLine($"enlist-agent: unknown option '{flag}'.");
                    return 2;
            }

            // Every flag above consumes its value, so step past it - otherwise the value itself is
            // read as the next flag and refused.
            i++;
        }

        if (runnerBinDirectory is null)
        {
            Console.Error.WriteLine(
                "Usage: enlist-agent --runner-bin <path> (--assignments <path> | --control-plane <url> [--agent <name>] [--join-token <token>]) [--legacy-runner-bin <path>] [--data <path>] [--container-image <image>] [--container-engine docker|wslc]");
            return 2;
        }

        if (controlPlaneUrl is not null && assignmentsPath is not null)
        {
            Console.Error.WriteLine("Specify either --assignments or --control-plane, not both.");
            return 2;
        }


        // The engine behind --container-image: Docker Desktop unless told otherwise. wslc is the
        // Windows Subsystem for Linux container CLI that ships with WSL 2.9.11+ — the same runner image,
        // built with `wslc build` from the same Dockerfile, with no Docker on the host at all.
        IContainerEngine? containerEngine = null;
        if (containerImage is not null)
        {
            containerEngine = (containerEngineName ?? "docker").ToLowerInvariant() switch
            {
                "docker" => new DockerContainerEngine(),
                "wslc" => new WslContainerEngine(),
                _ => null,
            };

            if (containerEngine is null)
            {
                Console.Error.WriteLine($"--container-engine must be 'docker' or 'wslc', got '{containerEngineName}'.");
                return 2;
            }
        }

        var stagingRoot = Path.Combine(dataRoot, "Runners");
        var logRoot = Path.Combine(dataRoot, "Logs");

        IReadOnlyDictionary<string, string>? additionalRunnerBinDirectories = legacyRunnerBinDirectory is null
            ? null
            : new Dictionary<string, string> { [RuntimeFlavors.NetFramework472] = legacyRunnerBinDirectory };

        AgentHost agentHost;
        string startupDescription;

        if (controlPlaneUrl is not null)
        {
            var effectiveAgentName = agentName ?? Environment.MachineName;
            if (!Uri.TryCreate(controlPlaneUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https"))
            {
                Console.Error.WriteLine($"--control-plane must be an absolute http(s) URL, got '{controlPlaneUrl}'.");
                return 2;
            }


            // What this agent presents to the control plane is settled before anything else is built:
            // the stored credential, a fresh enrollment with --join-token, or nothing when the control
            // plane says its authentication is off. Each outcome, and each refusal, goes to the agent
            // log as well as the console - under the SCM there is no console, and "the service failed
            // to start" with no reason in <data>\Logs is the worst kind of morning.
            var startupLog = new AgentFileLogSink(logRoot);
            void LogStartup(string line)
            {
                Console.WriteLine(line);
                startupLog.WriteAgentLogAsync(line).GetAwaiter().GetResult();
            }

            AgentCredential credential;
            try
            {
                credential = await AgentEnrollment.ResolveAsync(baseUri, effectiveAgentName, new AgentCredentialStore(dataRoot), joinToken, LogStartup, CancellationToken.None).ConfigureAwait(false);
            }
            catch (AgentStartupException ex)
            {
                Console.Error.WriteLine($"enlist-agent not starting: {ex.Message}");
                await startupLog.WriteAgentLogAsync($"Not starting: {ex.Message}").ConfigureAwait(false);
                return 2;
            }

            var packageCacheRoot = Path.Combine(dataRoot, "Packages");
            var assignmentSource = new ControlPlaneAssignmentSource(baseUri, effectiveAgentName, packageCacheRoot, credential: credential);

            // The same credential on every client that talks to the control plane, one handler each (a
            // DelegatingHandler belongs to one client); a refusal seen by any of them is said once.
            var reportHttp = new HttpClient(credential.CreateHandler()) { BaseAddress = baseUri };
            var statusReporter = new ControlPlaneStatusReporter(reportHttp, effectiveAgentName);

            var logHttp = new HttpClient(credential.CreateHandler()) { BaseAddress = baseUri };
            var logForwarder = new ControlPlaneLogForwarder(logHttp, effectiveAgentName);

            agentHost = new AgentHost(assignmentSource, runnerBinDirectory, stagingRoot, logRoot, statusReporter: statusReporter, logForwarder: logForwarder, additionalRunnerBinDirectories: additionalRunnerBinDirectories, containerImage: containerImage, agentName: effectiveAgentName, containerEngine: containerEngine);

            // Wired before ConnectAsync so a command pushed the instant the hub connects (unlikely,
            // but possible on a reconnect) is never missed between connecting and subscribing.
            assignmentSource.CommandReceived += command =>
                _ = agentHost.ExecuteCommandAsync(command.ApplicationName, command.TargetKind, command.TargetName, command.Action);

            await assignmentSource.ConnectAsync(CancellationToken.None).ConfigureAwait(false);

            startupDescription = $"agent '{effectiveAgentName}' against control plane {baseUri} ({(credential.HasToken ? "with its credential" : "no credential; authentication is off")})";
        }
        else
        {
            assignmentsPath ??= Path.Combine(AppContext.BaseDirectory, "assignments.json");

            AgentAssignments assignments;
            try
            {
                assignments = AssignmentStore.Load(assignmentsPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not load assignments from {assignmentsPath}: {ex.Message}");
                return 2;
            }

            agentHost = new AgentHost(assignments, runnerBinDirectory, stagingRoot, logRoot, additionalRunnerBinDirectories: additionalRunnerBinDirectories, containerImage: containerImage, containerEngine: containerEngine);
            startupDescription = $"{assignments.Applications.Count} assignment(s) from {assignmentsPath}";
        }

        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(o => o.ServiceName = "enlist-agent");
        builder.Services.AddSingleton(agentHost);
        builder.Services.AddHostedService<AgentBackgroundService>();

        // The generic host's own console lifetime already logs startup/shutdown; this line is just
        // for the interactive-terminal case, where Console.Out is still the real console (not yet
        // redirected — that's a per-runner thing, this process never touches its own Console.Out).
        Console.WriteLine($"enlist-agent starting: {startupDescription}");

        await builder.Build().RunAsync().ConfigureAwait(false);

        return 0;
    }
}
