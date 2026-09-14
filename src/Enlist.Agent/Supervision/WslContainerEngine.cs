using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Enlist.Agent.Supervision;

/// <summary>
/// <see cref="IContainerEngine"/> driven through `wslc`, the Windows Subsystem for Linux container
/// CLI that ships with WSL 2.9.11 and later — a second engine behind the same seam, so a host with no
/// Docker Desktop runs the same runner image (built with `wslc build` from the same Dockerfile) the
/// same way. Nothing above the seam knows which engine it is; that was the point of the seam.
///
/// `wslc` speaks the Docker CLI dialect (`run -d --name -p -v --label -e --network`, `stop -t`,
/// `remove -f`, `list -a -q --filter label=`, `inspect --format json`), with four differences this
/// class absorbs, each verified against wslc 2.9.11.0 before it was written down:
///
///   - A published port binds to 127.0.0.1 by default, the opposite of Docker's 0.0.0.0. That is
///     exactly right for the control channel and exactly wrong for an application port, so the
///     application's ports are published with an explicit `0.0.0.0:` prefix.
///   - Host port 0 ("let the engine choose") is rejected. `-p 5000` alone gets an engine-chosen
///     loopback port — the control channel's requirement, spelled differently — but there is no
///     "engine-chosen port on all interfaces", so a dynamic application port is chosen here, by
///     binding a free one and releasing it. That is a small race with anything else grabbing ports at
///     the same instant; the start fails loudly if it loses and the ordinary retry tries again.
///   - There is no `port` verb: the host port is read from `inspect`'s JSON.
///   - There is no `wait` verb: exit is polled from `inspect` once a second, which is how long a crash
///     may take to be noticed here versus immediately under Docker.
/// </summary>
public sealed class WslContainerEngine : CliContainerEngineBase, IContainerEngine
{
    private static readonly TimeSpan ExitPollInterval = TimeSpan.FromSeconds(1);

    /// <param name="executable">The CLI to drive. Null resolves to `wslc` on PATH, or WSL's own install directory when it is not — the installer puts it there without always putting it on PATH.</param>
    public WslContainerEngine(string? executable = null, TimeSpan? commandTimeout = null)
        : base(executable ?? DefaultExecutable(), commandTimeout)
    {
    }

    public string Name => "wslc";

    /// <summary>Where wslc lives on a machine where the WSL installer did not put it on PATH.</summary>
    public static string DefaultExecutable()
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WSL", "wslc.exe");
        return File.Exists(installed) ? installed : "wslc";
    }

    public async Task<string> RunAsync(ContainerSpec spec, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "run",

            // Never fetched from a registry - see CliContainerEngineBase.PullPolicy. wslc 2.9.11 takes
            // the same spelling as Docker.
            "--pull", PullPolicy,

            "--detach",
            "--name", spec.Name,

            // No host side at all: wslc picks a free host port AND binds it to 127.0.0.1 — the two
            // things the unauthenticated control channel needs. Its explicit spelling, 127.0.0.1:0:5000,
            // is rejected ("Invalid port '0'"), so this shorter form is the only one that works.
            "--publish", spec.ControlPort.ToString(CultureInfo.InvariantCulture),

            // Read-only, for the same reasons as under Docker — see DockerContainerEngine.RunAsync. A
            // Windows path in its native spelling is accepted as-is.
            "--volume", $"{spec.ApplicationPath}:/app:ro",
        };

        foreach (var (key, value) in spec.Labels ?? new Dictionary<string, string>())
        {
            args.Add("--label");
            args.Add($"{key}={value}");
        }

        // The APPLICATION's ports exist to be reached, so they get the all-interfaces prefix wslc does
        // not default to. A dynamic port has no engine-chosen spelling here (see the class summary), so
        // it is chosen locally and passed fixed.
        foreach (var port in spec.Ports ?? [])
        {
            var hostPort = port.HostPort ?? PickFreePort();
            args.Add("--publish");
            args.Add($"0.0.0.0:{hostPort}:{port.ContainerPort}/{port.Protocol}");
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
            // "No such image: enlist/runner:3.0.0" / "Error code: WSLC_E_IMAGE_NOT_FOUND", exit 1.
            if (result.Stderr.Contains("WSLC_E_IMAGE_NOT_FOUND", StringComparison.OrdinalIgnoreCase) ||
                result.Stderr.Contains("No such image", StringComparison.OrdinalIgnoreCase))
            {
                throw MissingImage("wslc", spec.Image, FirstLine(result.Stderr));
            }

            throw new InvalidOperationException($"wslc run failed ({result.ExitCode}): {FirstLine(result.Stderr)}");
        }

        // The id is the last non-empty line. With pulls disabled nothing should precede it, but a
        // progress or notice line from a later wslc must not be mistaken for the id.
        var containerId = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (string.IsNullOrEmpty(containerId))
        {
            throw new InvalidOperationException("wslc run reported success but printed no container id.");
        }

        return containerId;
    }

    public async Task<int> GetPublishedPortAsync(string containerId, int containerPort, string protocol = "tcp", CancellationToken ct = default)
    {
        using var document = await InspectAsync(containerId, ct).ConfigureAwait(false);

        // Docker-shaped: NetworkSettings.Ports["8080/tcp"] = [{ HostIp, HostPort }]. Found by name rather
        // than by path so a future wslc that moves the block does not silently break this.
        var ports = Find(document.RootElement, "Ports");
        if (ports is { ValueKind: JsonValueKind.Object } &&
            ports.Value.TryGetProperty($"{containerPort}/{protocol}", out var bindings) &&
            bindings.ValueKind == JsonValueKind.Array)
        {
            foreach (var binding in bindings.EnumerateArray())
            {
                if (binding.TryGetProperty("HostPort", out var hostPort) &&
                    int.TryParse(hostPort.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
                {
                    return port;
                }
            }
        }

        throw new InvalidOperationException($"Container {containerId} has no published host port for {containerPort}/{protocol}.");
    }

    /// <summary>
    /// wslc has no `wait`, so exit is polled: one `inspect` a second until the state says exited. A
    /// container that has been removed meanwhile makes inspect fail, which surfaces as
    /// InvalidOperationException — the same "already gone" StopAsync expects from Docker.
    /// </summary>
    public async Task<int> WaitForExitAsync(string containerId, CancellationToken ct = default)
    {
        while (true)
        {
            if (await TryReadExitCodeAsync(containerId, ct).ConfigureAwait(false) is { } exitCode)
            {
                return exitCode;
            }

            await Task.Delay(ExitPollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The container's exit code if it has exited, null while it is still running (or not yet started). Throws InvalidOperationException when inspect cannot find it at all.</summary>
    private async Task<int?> TryReadExitCodeAsync(string containerId, CancellationToken ct)
    {
        using var document = await InspectAsync(containerId, ct).ConfigureAwait(false);
        var state = Find(document.RootElement, "State");
        if (state is not { ValueKind: JsonValueKind.Object })
        {
            return null;
        }

        var running = state.Value.TryGetProperty("Running", out var r) && r.ValueKind == JsonValueKind.True;
        var status = state.Value.TryGetProperty("Status", out var s) ? s.GetString() : null;
        if (running || status is "created" or "restarting")
        {
            return null;
        }

        return state.Value.TryGetProperty("ExitCode", out var code) && code.TryGetInt32(out var exitCode) ? exitCode : -1;
    }

    public async Task<bool> StopAsync(string containerId, TimeSpan gracePeriod, CancellationToken ct = default)
    {
        var seconds = Math.Max(0, (int)Math.Ceiling(gracePeriod.TotalSeconds));

        // Same escalation as Docker's stop (the configured stop signal, then a kill after --time), and
        // the same reading of the exit code: 143 or 137 means it had to be signalled off.
        //
        // And the same budget: the grace period PLUS the ordinary command timeout, so a long
        // StopGracePeriod cannot be cut short by the flat default and reported as a kill that the
        // engine had not in fact performed yet.
        var result = await RunCliAsync(
            ["stop", "--time", seconds.ToString(CultureInfo.InvariantCulture), containerId],
            ct,
            timeout: gracePeriod + CommandTimeout).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            // wslc refuses to stop a container that has already exited — which is the ordinary case
            // here: the runner honoured the shutdown command it was sent a moment earlier and beat
            // the signal. Docker's stop returns 0 for that; this reads the exit code the container
            // already has and judges it the same way, rather than reporting a kill that never happened.
            try
            {
                var already = await TryReadExitCodeAsync(containerId, ct).ConfigureAwait(false);
                return already is { } code ? code is not (137 or 143) : false;
            }
            catch (InvalidOperationException)
            {
                // Already removed — nothing left to stop, and nothing to say it went badly.
                return true;
            }
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
            await RunCliAsync(["remove", "--force", containerId], ct).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort by contract — see IContainerEngine.RemoveAsync.
        }
    }

    /// <summary>
    /// `list` rather than `version`: the version prints from the CLI alone, while listing needs the
    /// container service to answer — and a service that does not answer is the state worth telling an
    /// operator about. The version string is then read separately, best-effort.
    /// </summary>
    public async Task<ContainerEngineProbe> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            var list = await RunCliAsync(["list", "--quiet"], ct).ConfigureAwait(false);
            if (list.ExitCode != 0)
            {
                var message = FirstLine(list.Stderr);
                return new ContainerEngineProbe(false, null, string.IsNullOrEmpty(message) ? $"wslc list exited {list.ExitCode}" : message);
            }

            var version = await RunCliAsync(["version"], ct).ConfigureAwait(false);
            var text = version.ExitCode == 0 ? version.Stdout.Trim() : "";
            var number = text.StartsWith("wslc ", StringComparison.OrdinalIgnoreCase) ? text["wslc ".Length..].Trim() : text;
            return new ContainerEngineProbe(true, string.IsNullOrEmpty(number) ? null : number);
        }
        catch (Exception ex)
        {
            // Covers wslc not being installed at all — Process.Start throws rather than exiting nonzero
            // when the executable does not exist.
            return new ContainerEngineProbe(false, null, ex.Message);
        }
    }

    public async Task<IReadOnlyList<string>> ListByLabelsAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct = default)
    {
        // --all: stopped containers count, for the same reason as under Docker.
        var args = new List<string> { "list", "--all", "--quiet" };
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

    private async Task<JsonDocument> InspectAsync(string containerId, CancellationToken ct)
    {
        var result = await RunCliAsync(["inspect", containerId, "--format", "json"], ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"wslc inspect failed ({result.ExitCode}): {FirstLine(result.Stderr)}");
        }

        try
        {
            return JsonDocument.Parse(result.Stdout);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"wslc inspect printed something that is not JSON: {ex.Message}", ex);
        }
    }

    /// <summary>Depth-first search for a property by name — inspect's document may wrap the object in an array or a session envelope.</summary>
    private static JsonElement? Find(JsonElement element, string propertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals(propertyName))
                    {
                        return property.Value;
                    }
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (Find(property.Value, propertyName) is { } found)
                    {
                        return found;
                    }
                }

                return null;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (Find(item, propertyName) is { } found)
                    {
                        return found;
                    }
                }

                return null;

            default:
                return null;
        }
    }

    /// <summary>A free TCP port on all interfaces, released again for wslc to bind. See the class summary for why this exists and what it risks.</summary>
    private static int PickFreePort()
    {
        var listener = new TcpListener(IPAddress.Any, 0);
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

    /// <summary>wslc's errors end with a boilerplate line about filing an issue; the first line is the one that says what happened.</summary>
    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
}
