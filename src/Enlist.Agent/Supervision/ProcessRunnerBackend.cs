using System.Diagnostics;

using Enlist.Agent.Hosting;
using Enlist.Agent.Logging;
using Enlist.Runner.Protocol;

namespace Enlist.Agent.Supervision;

/// <summary>
/// The <see cref="IRunnerBackend"/> that runs enlist-runner as a local child process on the same
/// machine as the agent, talking over a named pipe — i.e. everything enList has ever done. This is
/// the extracted body of what was <c>RunnerInstance.StartAsync</c>, verbatim in behaviour.
///
/// It owns the two pieces of that sequence that are specific to running a local process, and that
/// therefore have no place on the seam or in AgentHost: the Windows <see cref="JobObject"/> the
/// runner is enrolled in, and the connect timeout for the pipe handshake.
/// </summary>
public sealed class ProcessRunnerBackend : IRunnerBackend
{
    private readonly JobObject? _jobObject;
    private readonly TimeSpan _connectTimeout;
    private readonly AgentFileLogSink _logSink;
    private readonly RunnerTransport _transport;
    private readonly string _stagingRoot;

    /// <param name="jobObject">Windows-only and optional even there — null means the "agent death kills its runners" guarantee is simply unavailable, which is a warning, not a failure. See JobObject.cs.</param>
    /// <param name="transport">How the control channel is carried. Defaults to the named pipe this has always used; the socket option exists for a runner that cannot be reached by one (docs/03-architecture/Container-Story.md §6.1).</param>
    /// <param name="stagingRoot">Where per-application runner copies are staged. Owned here rather than by AgentHost as of C3: staging is a purely process-shaped step, and leaving it upstream meant every containerized application still paid for a runner copy nobody would read.</param>
    public ProcessRunnerBackend(JobObject? jobObject, TimeSpan connectTimeout, AgentFileLogSink logSink, string stagingRoot, RunnerTransport transport = RunnerTransport.NamedPipe)
    {
        _jobObject = jobObject;
        _connectTimeout = connectTimeout;
        _logSink = logSink;
        _stagingRoot = stagingRoot;
        _transport = transport;
    }

    public async Task<IRunnerInstance> StartAsync(RunnerStartRequest request)
    {
        var (applicationName, runnerBinDirectory, applicationPath, _, onMessage) = request;

        // A private copy per concurrently-running instance, with the exe renamed to the application
        // name so Task Manager shows "OrderProcessor.exe" rather than a wall of identical
        // "enlist-runner.exe" entries. See RunnerStaging.
        var runnerExecutablePath = Staging.RunnerStaging.Stage(runnerBinDirectory, _stagingRoot, applicationName);

        // The listener decides its own endpoint AND the arguments that reach it, so this method never
        // has to know whether it is handing out a pipe name or a socket path.
        IChannelListener listener = _transport == RunnerTransport.UnixSocket
            ? new UnixSocketChannelListener()
            : new NamedPipeChannelListener();

        var psi = new ProcessStartInfo(runnerExecutablePath) { UseShellExecute = false };
        foreach (var argument in listener.RunnerArguments)
        {
            psi.ArgumentList.Add(argument);
        }

        psi.ArgumentList.Add("--app");
        psi.ArgumentList.Add(applicationPath);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {runnerExecutablePath}");
        }
        catch
        {
            listener.Dispose();
            throw;
        }

        // Assigned immediately after Start, before anything can await and give the agent a chance to
        // die first — the whole point is that this process is never unsupervised, even for a moment.
        // Non-fatal if it fails (mirrors enList v2's AppInstanceProcess.CreateAsync): the runner still
        // starts and runs normally, it just loses the "killed automatically if the agent dies
        // abnormally" guarantee — warned about below rather than thrown, since a weakened safety net is
        // a warning-level concern, not a reason to refuse to start the application.
        var jobObjectAssigned = false;
        if (_jobObject is not null && OperatingSystem.IsWindows())
        {
            try
            {
                jobObjectAssigned = _jobObject.AssignProcess(process.Handle);
            }
            catch
            {
                jobObjectAssigned = false;
            }
        }

        Stream transport;
        try
        {
            using var connectCts = new CancellationTokenSource(_connectTimeout);
            transport = await listener.AcceptAsync(connectCts.Token).ConfigureAwait(false);
        }
        catch
        {
            listener.Dispose();
            ProcessRunnerInstance.KillQuietly(process);
            process.Dispose();
            throw;
        }

        // Safe after a successful accept, and required for the socket transport: it closes the LISTENING
        // endpoint (and unlinks the socket file) while leaving the accepted connection alone. The named
        // pipe listener deliberately no-ops here, because for a pipe those are the same object.
        listener.Dispose();

        if (_jobObject is not null && !jobObjectAssigned)
        {
            // Warned here rather than by AgentHost because the condition is
            // entirely about how THIS backend contains a process — AgentHost no longer knows a Job
            // Object exists, which is the point of the seam. Deliberately AFTER the connect wait, not
            // before it: a start that goes on to fail the handshake throws and logs its own failure,
            // and must not also emit this line, which it never did before.
            await _logSink.WriteAgentLogAsync(
                $"{applicationName}: could not assign its runner to the Job Object - it will NOT be killed " +
                "automatically if this agent exits abnormally.").ConfigureAwait(false);
        }

        var channel = new MessageChannel<AgentCommand, RunnerMessage>(transport);
        channel.MessageSkipped += line => _ = _logSink.WriteAgentLogAsync($"{applicationName}: {line}");
        var instance = new ProcessRunnerInstance(applicationName, process, transport, channel, onMessage, _logSink.WriteAgentLogAsync, jobObjectAssigned);
        instance.StartReceiving();
        return instance;
    }

    /// <inheritdoc />
    public void Cleanup(string applicationName) => Staging.RunnerStaging.Cleanup(_stagingRoot, applicationName);
}
