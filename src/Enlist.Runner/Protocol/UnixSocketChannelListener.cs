using System.Net.Sockets;

namespace Enlist.Runner.Protocol;

/// <summary>
/// A Unix domain socket the runner connects to, carrying the identical newline-delimited JSON a named
/// pipe carries. System.Net.Sockets is BCL, so this too stays within the zero-PackageReference rule.
///
/// Chosen over loopback TCP when containers were first being designed for (Container-Story.md §15,
/// decision 1), on the reasoning that a socket file bind-mounts in, needs no port, and is unreachable
/// from the host network.
///
/// That is NOT how containers ended up working. A Unix socket cannot cross a Windows-host to
/// Linux-container boundary, so the container backend dials in over TCP through the engine's port
/// proxy instead (`--listen`, Container-Story.md §12.3). This transport is still real and still
/// tested — host to host, on either platform — it simply is not what containers use. AF_UNIX is supported on Windows (10 1803+) as well as Linux, which is
/// what makes this transport testable on a Windows dev box rather than only inside a container.
///
/// Note the socket PATH is the endpoint here, where the pipe transport uses a bare NAME — hence
/// RunnerArguments existing on IChannelListener at all, so callers never have to know the difference.
/// </summary>
public sealed class UnixSocketChannelListener : IChannelListener
{
    private readonly Socket _listener;
    private readonly string _socketPath;

    /// <param name="socketPath">Where to create the socket file. Defaults to a unique path under the temp directory. Deliberately short: a Unix domain socket path is capped near 108 bytes on Linux, well below the usual filesystem limit, and a path that fits on Windows can still be rejected there.</param>
    public UnixSocketChannelListener(string? socketPath = null)
    {
        _socketPath = socketPath ?? Path.Combine(Path.GetTempPath(), "enl-" + Guid.NewGuid().ToString("N")[..12] + ".sock");

        // Bind fails outright if the path already exists — a stale file from a process that died
        // without unlinking is the classic cause, and it must not take out an unrelated new run.
        DeleteSocketFile();

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));

        // Backlog of 1: exactly one runner ever connects to a given listener, matching the named pipe's
        // maxNumberOfServerInstances: 1.
        _listener.Listen(1);
    }

    public IReadOnlyList<string> RunnerArguments => ["--socket", _socketPath];

    public async Task<Stream> AcceptAsync(CancellationToken ct = default)
    {
        var connection = await _listener.AcceptAsync(ct).ConfigureAwait(false);

        // ownsSocket: true — disposing the returned Stream closes this connection, which is the
        // ownership transfer IChannelListener.AcceptAsync promises. Unlike the named pipe case, the
        // listening socket below is a separate object and is still ours to close.
        return new NetworkStream(connection, ownsSocket: true);
    }

    /// <summary>
    /// Always closes the LISTENING socket and unlinks the socket file — neither is the connection, so
    /// unlike the named pipe listener this is safe (and necessary) after a successful accept. An
    /// accepted connection outlives this call.
    /// </summary>
    public void Dispose()
    {
        try
        {
            _listener.Dispose();
        }
        catch
        {
        }

        DeleteSocketFile();
    }

    /// <summary>Binding creates a real file on disk; nothing removes it automatically on either platform, so an un-deleted one is a leak that also blocks reuse of that exact path.</summary>
    private void DeleteSocketFile()
    {
        try
        {
            if (File.Exists(_socketPath))
            {
                File.Delete(_socketPath);
            }
        }
        catch
        {
        }
    }
}
