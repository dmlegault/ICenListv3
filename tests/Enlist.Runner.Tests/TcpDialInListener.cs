using System.Globalization;
using System.Net;
using System.Net.Sockets;

using Enlist.Runner.Protocol;

namespace Enlist.Runner.Tests;

/// <summary>
/// The --listen transport from the agent's side: the runner binds a port of this listener's choosing and
/// this end dials in, retrying until the runner is up — what ContainerRunnerBackend does, minus the
/// engine and its port proxy. Test-only, because the product's one caller of --listen is the container
/// backend, and it dials through an engine.
/// </summary>
internal sealed class TcpDialInListener(bool abortiveClose = false) : IChannelListener
{
    private readonly int _port = FreePort();

    public IReadOnlyList<string> RunnerArguments => ["--listen", _port.ToString(CultureInfo.InvariantCulture)];

    public async Task<Stream> AcceptAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, _port, ct);
            }
            catch (SocketException)
            {
                client.Dispose();
                await Task.Delay(100, ct);
                continue;
            }

            if (abortiveClose)
            {
                // A linger of zero makes closing send a reset rather than a FIN: the agent dying, as the
                // runner sees it from behind a port proxy.
                client.Client.LingerState = new LingerOption(true, 0);
            }

            // Owns the socket, so disposing the stream is what closes the connection — see IChannelListener.
            return client.GetStream();
        }
    }

    public void Dispose()
    {
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
}
