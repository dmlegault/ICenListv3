using System.IO.Pipes;

namespace Enlist.Runner.Protocol;

/// <summary>
/// The original transport, unchanged in behaviour: a one-instance named pipe server the runner dials
/// into as a client. System.IO.Pipes is BCL, so this stays within the zero-PackageReference rule.
///
/// Remains the default everywhere. Sockets exist for the case a named pipe cannot cross — a container
/// boundary — not because anything was wrong with pipes.
/// </summary>
public sealed class NamedPipeChannelListener : IChannelListener
{
    private readonly NamedPipeServerStream _pipe;
    private readonly string _pipeName;
    private bool _handedOver;

    public NamedPipeChannelListener(string? pipeName = null)
    {
        _pipeName = pipeName ?? "enlist-" + Guid.NewGuid().ToString("N");
        _pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    public IReadOnlyList<string> RunnerArguments => ["--pipe", _pipeName];

    public async Task<Stream> AcceptAsync(CancellationToken ct = default)
    {
        await _pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
        _handedOver = true;
        return _pipe;
    }

    /// <summary>
    /// Disposes the pipe ONLY if it was never handed to a caller. For a named pipe the listener and the
    /// live connection are the same object, so disposing here after a successful accept would tear down
    /// the very connection the caller is using — the reason IChannelListener specifies ownership
    /// transfer explicitly rather than leaving it to intuition.
    /// </summary>
    public void Dispose()
    {
        if (!_handedOver)
        {
            _pipe.Dispose();
        }
    }
}
