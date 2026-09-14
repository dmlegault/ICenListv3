using System.Globalization;

namespace Enlist.Agent.Supervision;

/// <summary>
/// <see cref="IContainerEngine"/> driven through the `docker` CLI.
///
/// The CLI rather than the Docker HTTP API on purpose. The API would mean either a NuGet client or
/// hand-rolled HTTP over a named pipe / Unix socket, and the agent gains nothing from either: every
/// operation here is a single short-lived command whose output is one line. The CLI is also what an
/// operator reaches for when diagnosing the same containers by hand, so what the agent does and what
/// they do stay directly comparable.
/// </summary>
public sealed class DockerContainerEngine : CliContainerEngineBase, IContainerEngine
{
    public DockerContainerEngine(string executable = "docker", TimeSpan? commandTimeout = null)
        : base(executable, commandTimeout)
    {
    }

    public string Name => "docker";

    public async Task<string> RunAsync(ContainerSpec spec, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "run",

            // Never fetched from a registry - see CliContainerEngineBase.PullPolicy.
            "--pull", PullPolicy,

            "--detach",
            "--name", spec.Name,

            // Host port 0 = let the engine pick a free one. Two runners for two applications must
            // never contend for a fixed port, and the agent asks for the real number afterwards.
            //
            // The 127.0.0.1 prefix is the security control, not a formality: the control channel has
            // NO authentication, and anything that can reach it can start services and run jobs.
            // Binding it to host loopback keeps it unreachable from the LAN.
            "--publish", $"127.0.0.1:0:{spec.ControlPort}",

            // Read-only: a plugin runs arbitrary author-supplied code, and it has no business
            // modifying the package it was loaded from. The package directory is also shared with the
            // package cache, where a write would corrupt a content-addressed digest for every other
            // consumer of it.
            "--volume", $"{spec.ApplicationPath}:/app:ro",
        };

        foreach (var (key, value) in spec.Labels ?? new Dictionary<string, string>())
        {
            args.Add("--label");
            args.Add($"{key}={value}");
        }

        // The APPLICATION's own ports. Note these are published WITHOUT the 127.0.0.1 prefix the control
        // channel above uses, and that asymmetry is deliberate rather than an oversight:
        //
        //   - the control channel is unauthenticated internal plumbing, so it must never leave the host;
        //   - an application port exists to BE reached, which is the entire point of declaring it.
        //
        // An empty host port lets the engine allocate — required for a tag-selector rule that lands on
        // many agents, where one hard-coded port cannot serve them all if two such applications coincide.
        foreach (var port in spec.Ports ?? [])
        {
            args.Add("--publish");
            args.Add(port.HostPort is {} hostPort
                ? $"{hostPort}:{port.ContainerPort}/{port.Protocol}"
                : $"{port.ContainerPort}/{port.Protocol}");
        }

        foreach (var network in spec.Networks ?? [])
        {
            args.Add("--network");
            args.Add(network);
        }

        foreach (var (key, value) in spec.Env ?? new Dictionary<string, string>())
        {
            args.Add("--env");
            args.Add($"{key}={value}");
        }

        args.Add(spec.Image);
        args.Add("--listen");
        args.Add(spec.ControlPort.ToString(CultureInfo.InvariantCulture));
        args.Add("--app");
        args.Add("/app");

        var result = await RunCliAsync(args, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            // "Error response from daemon: No such image: enlist/runner:3.0.0", exit 125.
            if (result.Stderr.Contains("No such image", StringComparison.OrdinalIgnoreCase))
            {
                throw MissingImage("docker", spec.Image, result.Stderr.Trim());
            }

            throw new InvalidOperationException($"docker run failed ({result.ExitCode}): {result.Stderr.Trim()}");
        }

        var containerId = result.Stdout.Trim();
        if (string.IsNullOrEmpty(containerId))
        {
            throw new InvalidOperationException("docker run reported success but printed no container id.");
        }

        return containerId;
    }

    public async Task<int> GetPublishedPortAsync(string containerId, int containerPort, string protocol = "tcp", CancellationToken ct = default)
    {
        var result = await RunCliAsync(["port", containerId, $"{containerPort}/{protocol}"], ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker port failed ({result.ExitCode}): {result.Stderr.Trim()}");
        }

        // Output is one or more "127.0.0.1:32768" lines (IPv4 and IPv6 bindings each get a line).
        // Taking the last colon-separated field tolerates both without parsing addresses.
        var first = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (first is null || first.LastIndexOf(':') < 0 ||
            !int.TryParse(first[(first.LastIndexOf(':') + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            throw new InvalidOperationException($"Could not read a published port for container {containerId} from: '{result.Stdout.Trim()}'");
        }

        return port;
    }

    /// <summary>`docker wait` blocks until the container exits and prints its exit code — the container equivalent of Process.WaitForExitAsync, and why no polling loop is needed here.</summary>
    public async Task<int> WaitForExitAsync(string containerId, CancellationToken ct = default)
    {
        // The one legitimately long-lived command: it returns when the container does.
        var result = await RunCliAsync(["wait", containerId], ct, Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker wait failed ({result.ExitCode}): {result.Stderr.Trim()}");
        }

        return int.TryParse(result.Stdout.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : -1;
    }

    public async Task<bool> StopAsync(string containerId, TimeSpan gracePeriod, CancellationToken ct = default)
    {
        var seconds = Math.Max(0, (int)Math.Ceiling(gracePeriod.TotalSeconds));

        // docker stop sends SIGTERM, waits, then SIGKILLs — the same escalation ProcessRunnerInstance
        // performs by hand, done by the engine instead. It exits 0 either way, so its exit code cannot
        // distinguish a graceful stop from a kill; the container's own exit code can. 143 is
        // 128+SIGTERM, i.e. terminated by the signal after refusing to leave; 137 is 128+SIGKILL.
        // The CLI is given the grace period PLUS the ordinary command budget, because this is the one
        // call whose duration the caller chooses. Left at the flat 60 s default, any StopGracePeriod
        // near or above it had the agent kill the CLI before the engine finished escalating - and then
        // report the container as killed while it was in fact still shutting down, which is precisely
        // backwards.
        var result = await RunCliAsync(
            ["stop", "--timeout", seconds.ToString(CultureInfo.InvariantCulture), containerId],
            ct,
            timeout: gracePeriod + CommandTimeout).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return false;
        }

        try
        {
            var exitCode = await WaitForExitAsync(containerId, ct).ConfigureAwait(false);
            return exitCode is not (137 or 143);
        }
        catch (InvalidOperationException)
        {
            // Already gone — nothing left to wait on, and the stop above did succeed.
            return true;
        }
    }

    public async Task RemoveAsync(string containerId, CancellationToken ct = default)
    {
        try
        {
            await RunCliAsync(["rm", "--force", containerId], ct).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort by contract — see IContainerEngine.RemoveAsync.
        }
    }

    /// <summary>
    /// `docker version` against the SERVER, not the client. `docker` being on PATH proves nothing —
    /// the CLI installs separately from the engine, and querying the server is what actually fails when
    /// the daemon is down. That distinction is not academic: this machine had the CLI present and the
    /// daemon stopped mid-session, which is exactly the state an operator needs told.
    /// </summary>
    public async Task<ContainerEngineProbe> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await RunCliAsync(["version", "--format", "{{.Server.Version}}"], ct).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                var message = result.Stderr.Trim();
                return new ContainerEngineProbe(false, null, string.IsNullOrEmpty(message) ? $"docker version exited {result.ExitCode}" : message);
            }

            var version = result.Stdout.Trim();
            return new ContainerEngineProbe(true, string.IsNullOrEmpty(version) ? null : version);
        }
        catch (Exception ex)
        {
            // Covers the engine not being installed at all — Process.Start throws rather than exiting
            // nonzero when the executable does not exist.
            return new ContainerEngineProbe(false, null, ex.Message);
        }
    }

    public async Task<IReadOnlyList<string>> ListByLabelsAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct = default)
    {
        // -a: stopped containers count. An exited container still holds its name and its disk, so it is
        // exactly the orphan worth clearing.
        var args = new List<string> { "ps", "-a", "--quiet" };
        foreach (var (key, value) in labels)
        {
            args.Add("--filter");
            args.Add($"label={key}={value}");
        }

        var result = await RunCliAsync(args, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return [];
        }

        return result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
