using Microsoft.Extensions.Configuration;

namespace Enlist.ControlPlane.Contracts;

/// <summary>
/// The hard rules of Authentication-Design.md section 8, as a pure function over the configured
/// listen URLs so they can be reasoned about without a server, and shared by the control plane and
/// the portal (R3: the portal has the same two rules). Authentication may be Off only when every
/// listener is loopback, and when it is Required no listener off loopback may be plain HTTP.
/// Returns the message to refuse startup with, or null when the configuration is acceptable.
/// </summary>
public static class ListenerRules
{
    /// <summary>What Kestrel listens on when nothing is configured at all.</summary>
    private const string KestrelDefault = "http://localhost:5000";

    /// <summary>
    /// Every place a listen address can come from, in ONE place because both hosts ask the same
    /// question and a second copy is how this drifts: --urls / ASPNETCORE_URLS (the "urls" key),
    /// Kestrel endpoint configuration, and ASPNETCORE_HTTP_PORTS / ASPNETCORE_HTTPS_PORTS (which
    /// arrive as the HTTP_PORTS / HTTPS_PORTS keys and bind every interface, not loopback).
    ///
    /// The port variables matter more than they look: the official ASP.NET Core container images
    /// set ASPNETCORE_HTTP_PORTS=8080 themselves, so a containerised control plane has a
    /// non-loopback listener that nobody wrote down anywhere. Reading only "urls" and Kestrel
    /// config, as this did until 2026-09-12, meant Mode=Off in a container passed the rule while
    /// binding 0.0.0.0 - the exact state the rule exists to make unconfigurable.
    ///
    /// Deliberately a UNION rather than a model of Kestrel's precedence. Where the sources disagree
    /// this may refuse a configuration that would in fact have bound only loopback; that costs a
    /// startup and a clear message, whereas guessing precedence wrongly in the other direction
    /// costs an unauthenticated control plane on a network.
    /// </summary>
    public static IReadOnlyList<string> ConfiguredUrls(IConfiguration configuration)
    {
        var urls = new List<string>();

        if (configuration["urls"] is { } fromUrls && !string.IsNullOrWhiteSpace(fromUrls))
        {
            urls.AddRange(fromUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            if (endpoint["Url"] is { } url && !string.IsNullOrWhiteSpace(url))
            {
                urls.Add(url);
            }
        }

        AddPorts(configuration["HTTP_PORTS"], "http", urls);
        AddPorts(configuration["HTTPS_PORTS"], "https", urls);

        return urls;
    }

    /// <summary>A bare port list binds every interface, so each port becomes "scheme://*:port" - not loopback, by Kestrel's own rule.</summary>
    private static void AddPorts(string? ports, string scheme, List<string> urls)
    {
        if (string.IsNullOrWhiteSpace(ports))
        {
            return;
        }

        foreach (var port in ports.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            urls.Add($"{scheme}://*:{port}");
        }
    }

    /// <param name="component">Named in the message: "control plane" or "portal".</param>
    public static string? Violation(IEnumerable<string> urls, string? mode, string component)
    {
        if (!AuthenticationModes.IsKnown(mode))
        {
            return $"Authentication:Mode must be '{AuthenticationModes.Required}' or '{AuthenticationModes.Off}', got '{mode}'.";
        }

        var off = AuthenticationModes.IsOff(mode);

        var list = urls.Select(u => u.Trim()).Where(u => u.Length > 0).ToList();
        if (list.Count == 0)
        {
            list.Add(KestrelDefault);
        }

        foreach (var url in list)
        {
            var (scheme, host) = Split(url);
            var loopback = IsLoopback(host);

            if (off && !loopback)
            {
                return $"Authentication is Off, but the {component} listens on '{url}', off loopback. Off is honoured only when every listener is bound to " +
                       $"localhost, 127.0.0.1 or ::1, and there is no override: an unauthenticated {component} reachable from a network " +
                       "cannot be configured. Bind to loopback, or set Authentication:Mode to Required. See docs/03-architecture/Authentication-Design.md section 8.";
            }

            if (!off && !loopback && scheme == "http")
            {
                return $"Authentication is Required, but the {component} listens on '{url}', plain HTTP off loopback, which would carry credentials in the clear. " +
                       "Use https:// with a Kestrel certificate, or bind to loopback. See docs/03-architecture/Authentication-Design.md section 9.";
            }
        }

        return null;
    }

    /// <summary>Hand-parsed rather than System.Uri, because Kestrel's own spellings ("http://+:5293", "http://*:5293") are not valid URIs.</summary>
    private static (string Scheme, string Host) Split(string url)
    {
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        var scheme = schemeEnd < 0 ? "http" : url[..schemeEnd].ToLowerInvariant();
        var rest = schemeEnd < 0 ? url : url[(schemeEnd + 3)..];

        var pathStart = rest.IndexOf('/');
        if (pathStart >= 0)
        {
            rest = rest[..pathStart];
        }

        if (rest.StartsWith('['))
        {
            var close = rest.IndexOf(']');
            return (scheme, close < 0 ? rest : rest[1..close]);
        }

        var portStart = rest.LastIndexOf(':');
        return (scheme, portStart < 0 ? rest : rest[..portStart]);
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host == "::1" ||
        host.StartsWith("127.", StringComparison.Ordinal);
}
