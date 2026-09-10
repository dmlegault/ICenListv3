namespace Enlist.Agent.Supervision;

/// <summary>
/// Which transport <see cref="ProcessRunnerBackend"/> carries the agent↔runner control channel over.
/// Both speak the identical protocol (see Enlist.Runner.Protocol.MessageChannel, which only ever
/// wanted a Stream) — this selects how the connection is established, nothing about what crosses it.
/// </summary>
public enum RunnerTransport
{
    /// <summary>The default, and what enList has always used: a one-instance named pipe per runner.</summary>
    NamedPipe,

    /// <summary>
    /// A Unix domain socket. Exists because a named pipe does not cross a container boundary while a
    /// socket file can simply be bind-mounted in (docs/03-architecture/Container-Story.md §15, decision 1). AF_UNIX
    /// works on Windows 10 1803+ as well as Linux, so this is exercised by the test suite on Windows
    /// rather than only becoming real once containers land.
    /// </summary>
    UnixSocket,
}
