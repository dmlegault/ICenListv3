using System.Runtime.Versioning;

namespace Enlist.ControlPlane.Contracts;

/// <summary>
/// <c>grant-certificate-access --thumbprint &lt;t&gt; --account &lt;a&gt;</c> - lets a service account read
/// the private key of the certificate it is about to serve TLS with.
///
/// WHY IT IS A VERB AND NOT PART OF STARTUP: a service cannot grant itself access it does not have.
/// The installer runs elevated and does it once, between installing the package and starting the
/// service - the same shape as enrolling the agent and storing the portal's key.
///
/// On BOTH hosts, and shared rather than copied, because a portal-only install has no control plane
/// executable to borrow a verb from and a control-plane-only install has no portal. It lives in
/// Contracts with no framework reference, so nothing here reaches the agent as a dependency.
///
/// Exit codes: 0 granted, 2 anything else. Nothing secret passes through it - a thumbprint is public
/// by design, which is precisely why the installer can put one on a command line.
/// </summary>
public static class CertificateCli
{
    public const string Verb = "grant-certificate-access";

    public static bool IsVerb(string[] args) => args.Length > 0 && args[0].Equals(Verb, StringComparison.OrdinalIgnoreCase);

    [SupportedOSPlatform("windows")]
    public static int Run(string[] args)
    {
        string? thumbprint = null;
        string? account = null;

        for (var i = 1; i < args.Length; i++)
        {
            if (i + 1 >= args.Length)
            {
                return Usage($"{args[i]} needs a value.");
            }

            switch (args[i])
            {
                case "--thumbprint": thumbprint = args[++i]; break;
                case "--account": account = args[++i]; break;
                default: return Usage($"unknown option '{args[i]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return Usage($"{Verb} needs --thumbprint.");
        }

        if (string.IsNullOrWhiteSpace(account))
        {
            return Usage($"{Verb} needs --account.");
        }

        var normalized = ServerCertificate.Normalize(thumbprint);
        if (normalized.Length == 0)
        {
            return Usage($"'{thumbprint}' is not a certificate thumbprint.");
        }

        try
        {
            using var certificate = ServerCertificate.FindByThumbprint(normalized);
            if (certificate is null)
            {
                Console.Error.WriteLine(
                    $"No certificate with thumbprint {normalized} was found in LocalMachine\\My or CurrentUser\\My.");
                return 2;
            }

            var keyFile = WindowsSecrets.GrantPrivateKeyAccess(certificate, account!);
            Console.WriteLine($"'{account}' can now read the private key for {normalized} ({keyFile}).");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            Console.Error.WriteLine($"Could not grant access to the certificate's private key: {ex.Message}");
            return 2;
        }
    }

    private static int Usage(string error)
    {
        Console.Error.WriteLine(error);
        Console.Error.WriteLine($"Usage: {Verb} --thumbprint <thumbprint> --account <account>");
        return 2;
    }
}
