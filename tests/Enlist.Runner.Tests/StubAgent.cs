using System.Diagnostics;

using Enlist.Runner.Protocol;

namespace Enlist.Runner.Tests;

/// <summary>
/// The "stub driver on the other end of the pipe" the design doc calls for (section 11) — plays the
/// agent's role just enough to drive a real enlist-runner.exe process through its lifecycle over a
/// real transport, with no in-process shortcuts.
///
/// Parameterized by <see cref="IChannelListener"/> so the identical lifecycle can be driven over
/// either a named pipe or a Unix domain socket. That is the whole point: the socket transport is not
/// proven by existing in the source, it is proven by the real runner completing the same lifecycle
/// over it.
///
/// Drives either runner build. The modern one is `dotnet enlist-runner.dll`; the net472 one
/// (<see cref="StartLegacyAsync"/>) is `enlist-runner.exe` run directly. Everything after the process
/// starts is the same code here, which is what a test of the legacy runner is for: the same stub, the
/// same protocol, the same expectations, against the other runtime.
/// </summary>
internal sealed class StubAgent : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Stream _transport;

    public MessageChannel<AgentCommand, RunnerMessage> Channel { get; }

    private StubAgent(Process process, Stream transport)
    {
        _process = process;
        _transport = transport;
        Channel = new MessageChannel<AgentCommand, RunnerMessage>(transport);
    }

    /// <summary>Named pipe — the default transport, and what every pre-existing test exercises.</summary>
    public static Task<StubAgent> StartAsync(string appDirectory, TimeSpan connectTimeout) =>
        StartAsync(appDirectory, connectTimeout, new NamedPipeChannelListener("enlist-test-" + Guid.NewGuid().ToString("N")));

    public static Task<StubAgent> StartAsync(string appDirectory, TimeSpan connectTimeout, IChannelListener listener)
    {
        var runnerDll = Path.Combine(AppContext.BaseDirectory, "enlist-runner.dll");
        if (!File.Exists(runnerDll))
        {
            throw new FileNotFoundException($"enlist-runner.dll not found next to the test assembly at {runnerDll} - expected the ProjectReference to Enlist.Runner to copy it there.", runnerDll);
        }

        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        psi.ArgumentList.Add(runnerDll);

        return StartAsync(psi, appDirectory, connectTimeout, listener);
    }

    /// <summary>
    /// The net472 runner, over a named pipe — the only transport it has. Not a ProjectReference (a
    /// net472 executable cannot be referenced from a net10.0 test project, and it has to be launched as
    /// a process anyway), so it is located by path like the control plane is in Enlist.TestSupport.
    /// </summary>
    public static Task<StubAgent> StartLegacyAsync(string appDirectory, TimeSpan connectTimeout)
    {
        var exe = RepoPaths.LegacyRunnerExe();
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException($"the net472 runner was not found at {exe} - build src/Enlist.Runner.Legacy first (dotnet build enList_v3.slnx builds it).", exe);
        }

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };

        return StartAsync(psi, appDirectory, connectTimeout, new NamedPipeChannelListener("enlist-test-" + Guid.NewGuid().ToString("N")));
    }

    private static async Task<StubAgent> StartAsync(ProcessStartInfo psi, string appDirectory, TimeSpan connectTimeout, IChannelListener listener)
    {
        foreach (var argument in listener.RunnerArguments)
        {
            psi.ArgumentList.Add(argument);
        }

        psi.ArgumentList.Add("--app");
        psi.ArgumentList.Add(appDirectory);

        var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {psi.FileName}");

        Stream transport;
        try
        {
            using var cts = new CancellationTokenSource(connectTimeout);
            transport = await listener.AcceptAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            listener.Dispose();
            KillQuietly(process);
            throw;
        }

        // Safe post-accept for both transports, and required for the socket one — see IChannelListener
        // on why the pipe listener no-ops here instead.
        listener.Dispose();

        return new StubAgent(process, transport);
    }

    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    /// <summary>
    /// Closes the transport WITHOUT killing the runner process — for simulating "the agent went away"
    /// and asserting the runner notices on its own. Disposing just the Channel wrapper is not enough: it
    /// wraps the stream with leaveOpen:true (see MessageChannel), so the underlying connection would
    /// stay live and the runner would never see a disconnect at all.
    /// </summary>
    public async Task ClosePipeAsync()
    {
        await Channel.DisposeAsync().ConfigureAwait(false);
        _transport.Dispose();
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Channel.DisposeAsync().ConfigureAwait(false);
        _transport.Dispose();
        KillQuietly(_process);
        _process.Dispose();
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
            // Best-effort cleanup only — a test that already failed shouldn't fail differently because cleanup raced the process exiting on its own.
        }
    }
}
