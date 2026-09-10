using Enlist.Agent.Configuration;
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

        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
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
            }
        }

        if (runnerBinDirectory is null)
        {
            Console.Error.WriteLine(
                "Usage: enlist-agent --runner-bin <path> (--assignments <path> | --control-plane <url> [--agent <name>]) [--legacy-runner-bin <path>] [--data <path>] [--container-image <image>] [--container-engine docker|wslc]");
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


            var packageCacheRoot = Path.Combine(dataRoot, "Packages");
            var assignmentSource = new ControlPlaneAssignmentSource(baseUri, effectiveAgentName, packageCacheRoot);

            var reportHttp = new HttpClient { BaseAddress = baseUri };
            var statusReporter = new ControlPlaneStatusReporter(reportHttp, effectiveAgentName);

            var logHttp = new HttpClient { BaseAddress = baseUri };
            var logForwarder = new ControlPlaneLogForwarder(logHttp, effectiveAgentName);

            agentHost = new AgentHost(assignmentSource, runnerBinDirectory, stagingRoot, logRoot, statusReporter: statusReporter, logForwarder: logForwarder, additionalRunnerBinDirectories: additionalRunnerBinDirectories, containerImage: containerImage, agentName: effectiveAgentName, containerEngine: containerEngine);

            // Wired before ConnectAsync so a command pushed the instant the hub connects (unlikely,
            // but possible on a reconnect) is never missed between connecting and subscribing.
            assignmentSource.CommandReceived += command =>
                _ = agentHost.ExecuteCommandAsync(command.ApplicationName, command.TargetKind, command.TargetName, command.Action);

            await assignmentSource.ConnectAsync(CancellationToken.None).ConfigureAwait(false);

            startupDescription = $"agent '{effectiveAgentName}' against control plane {baseUri}";
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
