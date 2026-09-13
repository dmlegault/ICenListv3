using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Enlist.TestSupport;

/// <summary>
/// Spawns the real Enlist.Portal.dll as its own OS process, bound to a free port, pointed at a
/// control plane the test already runs - the same "prove it against the real thing" approach as
/// <see cref="ControlPlaneTestServer"/>. What a test can then do is what a browser does: GET a page,
/// with or without Windows credentials, and read what was rendered. The environment carries the
/// portal's configuration: ControlPlane__BaseUrl, ControlPlane__ApiKey, Authentication__Mode and the
/// two Windows group names.
///
/// The default listener is HTTPS on every interface, and <see cref="BaseUri"/> names the machine by
/// its own name rather than 127.0.0.1, for a reason that took an afternoon: Windows refuses NTLM
/// authentication to the local machine under any name but its own (LSA's loopback check), so a
/// Windows sign-in test that dials 127.0.0.1 or localhost fails at the last step of the handshake
/// with no useful message. Dialing the machine name is exempt - and the portal's own rule R2 permits
/// a non-loopback listener only over HTTPS, hence the ASP.NET development certificate, which the
/// test clients accept without validating. A test of the Off mode passes an explicit loopback URL,
/// because Off is only permitted there.
///
/// Development, so the static web assets are served from the build output (Runbook 3.5); the pages'
/// HTML is what matters here, not the assets, but a wrong environment breaks the page render itself.
/// </summary>
public sealed class PortalTestServer : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _output;

    public Uri BaseUri { get; }

    /// <summary>Everything the portal wrote to stdout and stderr so far.</summary>
    public string Output
    {
        get
        {
            lock (_output)
            {
                return _output.ToString();
            }
        }
    }

    private PortalTestServer(Process process, Uri baseUri, StringBuilder output)
    {
        _process = process;
        BaseUri = baseUri;
        _output = output;
    }

    /// <summary>An HttpClient that accepts the development certificate and, when asked, signs in as the account running the tests - what an intranet browser does.</summary>
    public static HttpClient CreateClient(Uri baseUri, bool asWindowsUser)
    {
        var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = delegate { return true; };
        if (asWindowsUser)
        {
            handler.Credentials = System.Net.CredentialCache.DefaultCredentials;
        }

        return new HttpClient(handler) { BaseAddress = baseUri };
    }

    /// <summary>Throws, with the portal's own output, when it exits before answering - which is how a test proves a startup rule refused a configuration.</summary>
    public static async Task<PortalTestServer> StartAsync(TimeSpan readyTimeout, IReadOnlyDictionary<string, string> environment, string listenUrls = "https://0.0.0.0:0")
    {
        var dll = RepoPaths.PortalDll();
        if (!File.Exists(dll))
        {
            throw new FileNotFoundException($"Enlist.Portal.dll not found at {dll} - build src/Enlist.Portal first.", dll);
        }

        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("--urls");
        psi.ArgumentList.Add(listenUrls);
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        foreach (var (key, value) in environment)
        {
            psi.Environment[key] = value;
        }

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Enlist.Portal.");

        var output = new StringBuilder();

        async Task DrainAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (output)
                {
                    output.AppendLine(line);
                }
            }
        }

        _ = Task.Run(() => DrainAsync(process.StandardOutput));
        _ = Task.Run(() => DrainAsync(process.StandardError));

        string CapturedOutput()
        {
            lock (output) { return output.Length == 0 ? "(no output captured)" : output.ToString().Trim(); }
        }

        var deadline = DateTime.UtcNow + readyTimeout;
        Uri? baseUri = null;

        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                await Task.Delay(250).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"Enlist.Portal exited early with code {process.ExitCode} before becoming ready.{Environment.NewLine}" +
                    $"--- portal output ---{Environment.NewLine}{CapturedOutput()}");
            }

            if (baseUri is null)
            {
                var match = ListeningOn.Match(CapturedOutput());
                if (match.Success)
                {
                    baseUri = ClientAddress(match.Groups["scheme"].Value, match.Groups["host"].Value, match.Groups["port"].Value);
                }
            }

            if (baseUri is not null)
            {
                try
                {
                    // Any answer at all is "listening" - under Required an anonymous GET is a 401 with
                    // a Negotiate challenge, which is exactly the behaviour some tests then assert.
                    using var http = CreateClient(baseUri, asWindowsUser: false);
                    using var response = await http.GetAsync("/").ConfigureAwait(false);
                    return new PortalTestServer(process, baseUri, output);
                }
                catch (HttpRequestException)
                {
                }
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        ProcessKill.Quietly(process);
        throw new TimeoutException(
            $"Enlist.Portal did not become ready within {readyTimeout.TotalSeconds:0}s (address {baseUri?.ToString() ?? "never announced"}).{Environment.NewLine}" +
            $"--- portal output ---{Environment.NewLine}{CapturedOutput()}");
    }

    /// <summary>A listener on every interface is reached by the machine's own name (see the class summary); a loopback listener by the address it announced.</summary>
    private static Uri ClientAddress(string scheme, string host, string port)
    {
        var clientHost = host is "0.0.0.0" or "[::]" or "+" or "*" ? Environment.MachineName : host;
        return new Uri($"{scheme}://{clientHost}:{port}/");
    }

    public async ValueTask DisposeAsync()
    {
        ProcessKill.Quietly(_process);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
        }

        _process.Dispose();
    }

    private static readonly Regex ListeningOn = new(@"Now listening on:\s*(?<scheme>https?)://(?<host>\[[^\]]+\]|[^:/\s]+):(?<port>\d+)", RegexOptions.Compiled);
}
