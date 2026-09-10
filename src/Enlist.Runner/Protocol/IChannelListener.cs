namespace Enlist.Runner.Protocol;

/// <summary>
/// The server end of one agent↔runner connection, before the runner has dialled in.
///
/// This is the seam docs/03-architecture/Container-Story.md §6.1 identified: <see cref="MessageChannel{TOut, TIn}"/>
/// already takes a plain Stream, so the framing needs no abstraction at all — what varies is how that
/// Stream is *obtained*. A named pipe and a Unix domain socket differ only in how the endpoint is
/// created, named, and accepted; once accepted, both are just a Stream carrying identical bytes.
///
/// Lives in Enlist.Runner.Protocol beside MessageChannel, for the same reason MessageChannel does: it
/// belongs to the boundary itself, and both ends plus the test harness need it without either side
/// referencing the other's project.
/// </summary>
public interface IChannelListener : IDisposable
{
    /// <summary>
    /// The command-line arguments a runner must be started with to connect back to this listener —
    /// e.g. ["--pipe", "enlist-abc123"] or ["--socket", "/tmp/enlist-abc123.sock"]. Produced by the
    /// listener rather than assembled by the caller so the flag and the endpoint value can never
    /// disagree, and so adding a transport does not mean editing every launch site.
    /// </summary>
    IReadOnlyList<string> RunnerArguments { get; }

    /// <summary>
    /// Waits for the runner to connect and returns the connected Stream. Called exactly once.
    ///
    /// Ownership transfers to the caller: disposing the returned Stream closes the connection, and
    /// <see cref="IDisposable.Dispose"/> on the listener afterwards must not. (That distinction is
    /// real, not theoretical — for a named pipe the listener and the connection are the same object,
    /// while for a socket they are two.)
    /// </summary>
    Task<Stream> AcceptAsync(CancellationToken ct = default);
}
