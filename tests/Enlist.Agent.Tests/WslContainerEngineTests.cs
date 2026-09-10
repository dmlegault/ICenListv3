using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

using Enlist.Agent.Configuration;
using Enlist.Agent.Logging;
using Enlist.Agent.Supervision;
using Enlist.ControlPlane.Contracts;
using Enlist.TestSupport;

namespace Enlist.Agent.Tests;

/// <summary>
/// The second container engine, over the real `wslc` CLI and the `enlist/runner:wslc` image — the
/// same lifecycle the Docker classes prove, on the engine that needs no Docker Desktop. Driven through
/// the real <see cref="ContainerRunnerBackend"/> and, for the crash case, the real <see cref="AgentHost"/>,
/// so what is proven is the seam's second implementation, not the engine class in isolation.
///
/// SKIPPED AUTOMATICALLY when wslc or the image is unavailable. Build the image with:
///
///   wslc build -f src/Enlist.Runner/Dockerfile -t enlist/runner:wslc .
/// </summary>
public sealed class WslContainerEngineTests : IAsyncLifetime
{
    private const string Image = "enlist/runner:wslc";
    private const string AgentLabel = "enlist.agent";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "enlist-wslc-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _agentNames = [];
    private AgentHost? _host;
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(60);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
        }

        // Whatever a failed test left behind under this class's own ownership labels.
        foreach (var agent in _agentNames)
        {
            foreach (var id in await ListByAgentAsync(agent))
            {
                await WslcAsync("remove", "--force", id);
            }
        }

        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch
        {
        }
    }

    [SkippableFact]
    public async Task An_unmodified_package_runs_in_a_WSL_container_and_completes_discovery()
    {
        Skip.IfNot(await WslcAvailableAsync(), $"wslc or the {Image} image is unavailable.");

        var backend = CreateBackend(NewAgentName("wslc-run"));
        await using var instance = await backend.StartAsync(new RunnerStartRequest(
            RepoPaths.UniqueAppName(), "", RepoPaths.SampleServiceDir(), IsolationSpec.ProcessDefault, (_, _) => Task.CompletedTask));

        var ready = await instance.WhenReady.WaitAsync(Step);
        Assert.Contains(ready.Services, s => s.Name == "Sample Service");
        Assert.False(string.IsNullOrWhiteSpace(instance.RuntimeId));

        // The control channel is unauthenticated: it must be published on loopback and nowhere else.
        var bindings = await BindingsAsync(instance.RuntimeId!, "5000/tcp");
        Assert.NotEmpty(bindings);
        Assert.All(bindings, b => Assert.Contains(b.HostIp, new[] { "127.0.0.1", "::1" }));

        Assert.True(await instance.StopAsync(TimeSpan.FromSeconds(20)), "the container had to be killed rather than shutting down gracefully.");
    }

    [SkippableFact]
    public async Task Application_ports_land_on_all_interfaces_static_and_dynamic_and_are_reported()
    {
        Skip.IfNot(await WslcAvailableAsync(), $"wslc or the {Image} image is unavailable.");

        var staticPort = FreePort();
        var isolation = new IsolationSpec(IsolationModes.Container, Ports:
        [
            new PortMapping(8080, staticPort, "tcp", "http"),
            new PortMapping(8081, null, "tcp", "metrics"),
        ]);

        var backend = CreateBackend(NewAgentName("wslc-ports"));
        await using var instance = await backend.StartAsync(new RunnerStartRequest(
            RepoPaths.UniqueAppName(), "", RepoPaths.SampleServiceDir(), isolation, (_, _) => Task.CompletedTask));
        await instance.WhenReady.WaitAsync(Step);

        var http = Assert.Single(instance.Endpoints, e => e.Name == "http");
        var metrics = Assert.Single(instance.Endpoints, e => e.Name == "metrics");
        Assert.Equal(staticPort, http.HostPort);
        Assert.True(metrics.HostPort > 0, "the dynamic port was never resolved");

        // wslc binds published ports to loopback unless told otherwise — the engine must have told it.
        Assert.Contains(await BindingsAsync(instance.RuntimeId!, "8080/tcp"), b => b.HostIp is "0.0.0.0" or "::" && b.HostPort == staticPort);
        Assert.Contains(await BindingsAsync(instance.RuntimeId!, "8081/tcp"), b => b.HostIp is "0.0.0.0" or "::" && b.HostPort == metrics.HostPort);
    }

    [SkippableFact]
    public async Task A_killed_WSL_container_is_seen_as_a_crash_and_restarted()
    {
        Skip.IfNot(await WslcAvailableAsync(), $"wslc or the {Image} image is unavailable.");

        var appName = RepoPaths.UniqueAppName();
        var assignments = new AgentAssignments
        {
            Applications =
            [
                new ApplicationAssignment { Name = appName, Path = RepoPaths.SampleServiceDir(), Isolation = new IsolationSpec(IsolationModes.Container) },
            ],
        };

        _host = new AgentHost(
            new StaticAssignmentSource(assignments),
            RepoPaths.RunnerBinDirectory(),
            Path.Combine(_dataRoot, "Runners"),
            Path.Combine(_dataRoot, "Logs"),
            options: AgentHostOptions.Default,
            runnerBackends: new Dictionary<string, IRunnerBackend> { [IsolationModes.Container] = CreateBackend(NewAgentName("wslc-crash")) });

        await _host.StartAsync();
        Assert.True(await WaitForAsync(() => _host.Instances.ContainsKey(appName), TimeSpan.FromSeconds(90)), "the application never started in a WSL container.");

        var original = _host.Instances[appName].RuntimeId;
        Assert.False(string.IsNullOrWhiteSpace(original));

        // No ShutdownCommand, no cooperative anything — a crash, as far as the host can tell. It has to
        // come back through the same path a killed process does, on an engine the host cannot name.
        await WslcAsync("kill", original!);

        Assert.True(
            await WaitForAsync(() => _host.Instances.TryGetValue(appName, out var current) && current.RuntimeId != original, TimeSpan.FromSeconds(120)),
            "the application was never restarted after its WSL container was killed.");
    }

    [SkippableFact]
    public async Task Orphans_are_reaped_by_ownership_label_and_another_agents_containers_are_left_alone()
    {
        Skip.IfNot(await WslcAvailableAsync(), $"wslc or the {Image} image is unavailable.");

        var agent = NewAgentName("wslc-reap");
        var other = NewAgentName("wslc-other");

        // Abandoned, never disposed — what a killed agent leaves behind.
        var abandoned = await CreateBackend(agent).StartAsync(new RunnerStartRequest(
            RepoPaths.UniqueAppName(), "", RepoPaths.SampleServiceDir(), IsolationSpec.ProcessDefault, (_, _) => Task.CompletedTask));
        var live = await CreateBackend(other).StartAsync(new RunnerStartRequest(
            RepoPaths.UniqueAppName(), "", RepoPaths.SampleServiceDir(), IsolationSpec.ProcessDefault, (_, _) => Task.CompletedTask));

        await using (live)
        {
            Assert.Single(await ListByAgentAsync(agent));

            // The same agent starting up again.
            await CreateBackend(agent).ReapOrphansAsync();

            Assert.Empty(await ListByAgentAsync(agent));
            Assert.Single(await ListByAgentAsync(other));
        }
    }

    private string NewAgentName(string prefix)
    {
        var name = prefix + "-" + Guid.NewGuid().ToString("N")[..8];
        _agentNames.Add(name);
        return name;
    }

    private ContainerRunnerBackend CreateBackend(string agentName)
    {
        var logRoot = Path.Combine(_dataRoot, "Logs");
        Directory.CreateDirectory(logRoot);
        return new ContainerRunnerBackend(new WslContainerEngine(), Image, TimeSpan.FromSeconds(45), new AgentFileLogSink(logRoot, null), agentName);
    }

    /// <summary>Every host binding wslc reports for one container port, from `inspect` — IPv4 and IPv6 each get an entry, in no fixed order.</summary>
    private static async Task<IReadOnlyList<(string HostIp, int HostPort)>> BindingsAsync(string containerId, string portKey)
    {
        using var document = System.Text.Json.JsonDocument.Parse(await WslcAsync("inspect", containerId, "--format", "json"));
        var result = new List<(string, int)>();
        Collect(document.RootElement);
        return result;

        void Collect(System.Text.Json.JsonElement element)
        {
            if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name == "Ports" && property.Value.ValueKind == System.Text.Json.JsonValueKind.Object && property.Value.TryGetProperty(portKey, out var bindings) && bindings.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var binding in bindings.EnumerateArray())
                        {
                            result.Add((binding.GetProperty("HostIp").GetString() ?? "", int.Parse(binding.GetProperty("HostPort").GetString() ?? "0")));
                        }
                    }
                    else
                    {
                        Collect(property.Value);
                    }
                }
            }
            else if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item);
                }
            }
        }
    }

    private static async Task<IReadOnlyList<string>> ListByAgentAsync(string agentName)
    {
        var output = await WslcAsync("list", "--all", "--quiet", "--filter", $"label={AgentLabel}={agentName}");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Engine reachable AND the image present, in one call — either being absent means the same thing here.</summary>
    private static async Task<bool> WslcAvailableAsync()
    {
        try
        {
            if (!File.Exists(WslContainerEngine.DefaultExecutable()) && !OperatingSystem.IsWindows())
            {
                return false;
            }

            var images = await WslcAsync("images");
            return Regex.IsMatch(images, @"enlist/runner\s+wslc");
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> WslcAsync(params string[] args)
    {
        var psi = new ProcessStartInfo(WslContainerEngine.DefaultExecutable()) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (await stdout).Replace("\0", "").Replace("\r", "");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(250);
        }

        return condition();
    }
}
