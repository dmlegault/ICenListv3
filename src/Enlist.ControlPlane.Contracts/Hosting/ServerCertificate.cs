using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Configuration;

namespace Enlist.ControlPlane.Contracts;

/// <summary>
/// The certificate Kestrel serves HTTPS with, resolved the same way by the control plane and the
/// portal - shared for the reason <see cref="ListenerRules"/> is shared: both hosts ask the same
/// question, and a second copy is how the two drift.
///
/// WHY THIS EXISTS AT ALL. Authentication defaults to Required, Required refuses plain HTTP off
/// loopback, and both listen defaults are `+` - which is not loopback. So the first real deployment
/// is also the first TLS deployment, and a host with no certificate is a service that installs
/// cleanly and never starts. That is the failure this file is here to turn into a sentence.
///
/// BY THUMBPRINT, which ASP.NET Core's own certificate configuration cannot do. Its built-in
/// Kestrel:Certificates:Default supports a PFX path, or a store lookup by SUBJECT - and a subject is
/// not unique. Two certificates for the same host, one expired, is an ordinary state of affairs, and
/// picking between them by name is a coin toss that shows up as an expired certificate being served.
/// A thumbprint names exactly one certificate, which is why Installer-UI-Design section 12 chose it.
///
/// Kestrel's own configuration still works and is left alone: anyone who has already written
/// Kestrel:Certificates:Default keeps it, and this only steps in when Certificate:Thumbprint or
/// Certificate:Path is set.
/// </summary>
public static class ServerCertificate
{
    /// <summary>A thumbprint of a certificate in LocalMachine\My (falling back to CurrentUser\My for a developer).</summary>
    public const string ThumbprintSetting = "Certificate:Thumbprint";

    /// <summary>A PFX on disk, for a host that does not use the certificate store.</summary>
    public const string PathSetting = "Certificate:Path";

    /// <summary>That PFX's password. Never a command-line argument - see the remarks on Resolve.</summary>
    public const string PasswordSetting = "Certificate:Password";

    /// <summary>What ASP.NET Core reads for itself, checked so this does not report a certificate missing when Kestrel has one.</summary>
    public const string KestrelDefaultSection = "Kestrel:Certificates:Default";

    /// <summary>
    /// A thumbprint as a person will actually supply it.
    ///
    /// THIS IS THE ONE THAT BITES. Copying a thumbprint out of certmgr's Details tab gives it with
    /// spaces between every byte AND an invisible U+200E LEFT-TO-RIGHT MARK at the front, because the
    /// field is rendered for bidirectional text. Both survive a paste into a config file, neither is
    /// visible in one, and X509Certificate2Collection.Find matches on an exact string - so the
    /// certificate is "not found" while sitting in the store, looking identical to what was typed.
    /// </summary>
    public static string Normalize(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return "";
        }

        var cleaned = new System.Text.StringBuilder(thumbprint!.Length);
        foreach (var character in thumbprint)
        {
            if (Uri.IsHexDigit(character))
            {
                cleaned.Append(char.ToUpperInvariant(character));
                continue;
            }

            // Only the noise a thumbprint is legitimately copied WITH is ignored: whitespace, the
            // bidirectional marks certmgr inserts, and the separators some tools print.
            if (char.IsWhiteSpace(character) || character is '‎' or '‏' or '‪' or '‬' or ':' or '-')
            {
                continue;
            }

            // Anything else means this is not a thumbprint that lost its formatting - it is a
            // different string altogether. Silently keeping the hex letters out of it would turn
            // "the one in the email" into "EE", and then report THAT as the certificate not found.
            return "";
        }

        return cleaned.ToString();
    }

    /// <summary>
    /// Why this host will not start, or null when the configuration is serviceable. A pure function
    /// over the configured listeners and settings, so it can be reasoned about without a store.
    ///
    /// It asks only whether a certificate has been NAMED. Whether that certificate exists is
    /// <see cref="Load"/>'s business, and it has a better message for that case.
    /// </summary>
    public static string? Violation(IReadOnlyList<string> urls, IConfiguration configuration, string component, bool isDevelopment)
    {
        // DEVELOPMENT IS EXEMPT, and not as a convenience. Kestrel uses the ASP.NET Core development
        // certificate for an https listener with nothing configured - that is what `dotnet dev-certs
        // https` installs and what every `dotnet run` since 2.1 has relied on. Demanding explicit
        // configuration there would refuse the one environment where https already works.
        //
        // Outside Development there is no such certificate, so the same silence means a service that
        // binds and then fails every handshake. Same split, and for the same kind of reason, as the
        // migration check in the control plane's Program.cs.
        if (isDevelopment)
        {
            return null;
        }

        var https = urls.Where(IsHttps).ToList();
        if (https.Count == 0)
        {
            return null;
        }

        if (IsConfigured(configuration))
        {
            return null;
        }

        return
            $"The {component} is configured to listen on {string.Join(", ", https)}, which needs a TLS certificate, and none is configured. " +
            $"Set {ThumbprintSetting} to the thumbprint of a certificate in the machine's personal store (LocalMachine\\My), " +
            $"or {PathSetting} and {PasswordSetting} for a PFX file. " +
            "Authentication is Required unless configured otherwise, and Required refuses plain HTTP anywhere but loopback, so http is not the answer here.";
    }

    /// <summary>Whether anything has named a certificate - ours or Kestrel's own.</summary>
    public static bool IsConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration[ThumbprintSetting]) ||
        !string.IsNullOrWhiteSpace(configuration[PathSetting]) ||
        configuration.GetSection(KestrelDefaultSection).GetChildren().Any();

    private static bool IsHttps(string url) =>
        url.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The certificate itself, or null when this host has not been asked to supply one (Kestrel's own
    /// configuration, or plain HTTP on loopback).
    ///
    /// THE PASSWORD IS READ FROM CONFIGURATION AND NEVER FROM A COMMAND LINE. A service's binPath is
    /// readable by any local user out of the process list, which is the same rule the connection
    /// string, the portal's key and the join token all follow. A thumbprint is not a secret and may go
    /// anywhere; a PFX password may not.
    ///
    /// Throws with the reason rather than returning null when a certificate WAS named and cannot be
    /// produced - a host that silently falls back to no certificate would fail later, inside Kestrel,
    /// saying something about an endpoint.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static X509Certificate2? Load(IConfiguration configuration)
    {
        if (configuration[ThumbprintSetting] is { } configured && !string.IsNullOrWhiteSpace(configured))
        {
            var thumbprint = Normalize(configured);
            if (thumbprint.Length == 0)
            {
                throw new InvalidOperationException(
                    $"{ThumbprintSetting} is set to '{configured}', which contains no hexadecimal digits and so cannot be a thumbprint.");
            }

            return FindByThumbprint(thumbprint)
                ?? throw new InvalidOperationException(
                    $"No certificate with thumbprint {thumbprint} was found in LocalMachine\\My or CurrentUser\\My. " +
                    "Check it is installed on this machine, and that the thumbprint was copied whole - " +
                    "certmgr renders it with spaces and an invisible left-to-right mark, both of which are ignored here but not by every tool.");
        }

        if (configuration[PathSetting] is { } path && !string.IsNullOrWhiteSpace(path))
        {
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"{PathSetting} names {path}, which does not exist or is not readable by this service's account.");
            }

            // Persisted key set so the private key survives the certificate object, and machine key
            // set because a service has no user profile to keep one in.
            return X509CertificateLoader.LoadPkcs12FromFile(
                path,
                configuration[PasswordSetting],
                X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
        }

        return null;
    }

    /// <summary>
    /// LocalMachine first, because that is where a service's certificate belongs; CurrentUser after,
    /// so a developer running this from a terminal with a personal certificate is not stuck.
    ///
    /// validOnly: false deliberately. An expired or not-yet-valid certificate is FOUND and then
    /// reported as itself, which is a far better failure than "no certificate with that thumbprint" -
    /// the fix for the two is completely different.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static X509Certificate2? FindByThumbprint(string thumbprint)
    {
        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            using var store = new X509Store(StoreName.My, location);
            try
            {
                store.Open(OpenFlags.ReadOnly);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // A store this account cannot open is not an error worth failing on: the other one
                // may hold the certificate.
                continue;
            }

            var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            if (found.Count > 0)
            {
                return found[0];
            }
        }

        return null;
    }
}
