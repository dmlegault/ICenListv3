using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace Enlist.ControlPlane.Contracts;

/// <summary>
/// The two things every secret written to disk on this machine needs: who is allowed to read it, and
/// a closed ACL saying so.
///
/// Shared because the agent's credential file and the portal's appsettings.json face exactly the same
/// problem, and the interesting half of it is a trap rather than a policy - see
/// <see cref="ResolveAccount"/>. Two copies of that would be two chances to get it wrong.
///
/// Windows only. Both callers are services on Windows, and both say so.
/// </summary>
public static class WindowsSecrets
{
    /// <summary>
    /// A service account name as a SID, spelled the way a SERVICE DEFINITION spells it.
    ///
    /// THE BUILT-IN THREE DO NOT ROUND-TRIP. "LocalSystem" is what ServiceInstall, sc.exe and
    /// New-Service call that account, and translating that name throws IdentityNotMappedException -
    /// its real name is "NT AUTHORITY\SYSTEM". The two service accounts are the same story. So the
    /// names an installer will actually be handed are exactly the names the account database does not
    /// know, and the default configuration is the one that would fail.
    ///
    /// Anything else is a real account and is translated. An unresolvable name throws rather than
    /// being skipped: a secret quietly written without the reader that needs it produces a service
    /// that will not start, diagnosed far from here.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static SecurityIdentifier ResolveAccount(string account)
    {
        if (string.IsNullOrWhiteSpace(account))
        {
            throw new ArgumentException("An account name is required.", nameof(account));
        }

        var name = account.Trim();
        WellKnownSidType? known = name.ToUpperInvariant() switch
        {
            "LOCALSYSTEM" or "SYSTEM" or @"NT AUTHORITY\SYSTEM" => WellKnownSidType.LocalSystemSid,
            "NETWORKSERVICE" or "NETWORK SERVICE" or @"NT AUTHORITY\NETWORKSERVICE" or @"NT AUTHORITY\NETWORK SERVICE" => WellKnownSidType.NetworkServiceSid,
            "LOCALSERVICE" or "LOCAL SERVICE" or @"NT AUTHORITY\LOCALSERVICE" or @"NT AUTHORITY\LOCAL SERVICE" => WellKnownSidType.LocalServiceSid,
            _ => null,
        };

        if (known is { } wellKnown)
        {
            return new SecurityIdentifier(wellKnown, null);
        }

        try
        {
            return (SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException)
        {
            throw new InvalidOperationException(
                $"The account '{account}' could not be found on this machine or its domain, so a secret cannot be made readable by it. " +
                "Check the account the service is configured to run as.");
        }
    }

    /// <summary>
    /// SYSTEM, the local Administrators group, whoever this process runs as, and optionally the
    /// account a service will run as. Duplicates merge.
    ///
    /// The last one is what an INSTALLER needs and a service does not: when a service writes its own
    /// secret the writer is already the reader, but when setup writes it on the service's behalf the
    /// writer is an elevated operator and the service account is a stranger to the file.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IEnumerable<SecurityIdentifier> Readers(string? alsoReadableBy = null)
    {
        yield return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        yield return new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        using var current = WindowsIdentity.GetCurrent();
        if (current.User is { } user)
        {
            yield return user;
        }

        if (!string.IsNullOrWhiteSpace(alsoReadableBy))
        {
            yield return ResolveAccount(alsoReadableBy!);
        }
    }

    /// <summary>
    /// An ACL that grants only the identities given. Inheritance is OFF rather than layered on
    /// whatever the parent directory happens to allow - %ProgramData% and %ProgramFiles% both grant
    /// Users read by default, which for a secret is the whole problem.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static FileSecurity ClosedTo(IEnumerable<SecurityIdentifier> readers)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var identity in readers)
        {
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }

    /// <summary>
    /// Lets a service account read a certificate's PRIVATE KEY, which is a separate permission from
    /// reading the certificate and is the one everybody forgets.
    ///
    /// A certificate in LocalMachine\My is readable by anyone; its private key is a file under
    /// %ProgramData%\Microsoft\Crypto with an ACL of its own, and importing a PFX grants that only to
    /// the account that did the importing. So a certificate installed by an administrator and served
    /// by a service running as NETWORK SERVICE - the default for both the control plane and the
    /// portal - produces a host that starts, binds, and then fails every TLS handshake. The
    /// certificate is right there, and the error talks about the endpoint.
    ///
    /// Read is granted rather than full control: serving TLS needs to use the key, never to change or
    /// delete it. Returns the key file it touched, for a caller that wants to say so.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string GrantPrivateKeyAccess(X509Certificate2 certificate, string account)
    {
        if (certificate == null)
        {
            throw new ArgumentNullException(nameof(certificate));
        }

        if (!certificate.HasPrivateKey)
        {
            throw new InvalidOperationException(
                $"The certificate {certificate.Thumbprint} has no private key in this store, so it cannot serve TLS. " +
                "It was probably imported without one - re-import the PFX, including the private key.");
        }

        var keyFile = PrivateKeyFile(certificate)
            ?? throw new InvalidOperationException(
                $"The private key for {certificate.Thumbprint} is not a file this process can find - it may be held in a hardware or custom key store provider, " +
                $"in which case '{account}' has to be granted access through that provider's own tools.");

        var info = new FileInfo(keyFile);
        var security = info.GetAccessControl();

        // ADDED to whatever is there, not replacing it: the account that imported the certificate,
        // and SYSTEM, must keep the access they have. This is the opposite of ClosedTo above, and
        // deliberately so - this file is not ours to lock down.
        security.AddAccessRule(new FileSystemAccessRule(
            ResolveAccount(account),
            FileSystemRights.Read,
            AccessControlType.Allow));

        info.SetAccessControl(security);
        return keyFile;
    }

    /// <summary>
    /// Where a private key actually lives. Two shapes, because Windows has two generations of key
    /// storage and a certificate may use either: CNG keys are named files under Crypto\Keys, and the
    /// older CAPI keys live under Crypto\RSA\MachineKeys.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? PrivateKeyFile(X509Certificate2 certificate)
    {
        var names = new List<string>();

        using (var rsa = certificate.GetRSAPrivateKey())
        {
            if (rsa is System.Security.Cryptography.RSACng cng)
            {
                names.Add(cng.Key.UniqueName ?? "");
            }
            else if (rsa is System.Security.Cryptography.RSACryptoServiceProvider capi)
            {
                names.Add(capi.CspKeyContainerInfo.UniqueKeyContainerName ?? "");
            }
        }

        using (var ecdsa = certificate.GetECDsaPrivateKey())
        {
            if (ecdsa is System.Security.Cryptography.ECDsaCng cng)
            {
                names.Add(cng.Key.UniqueName ?? "");
            }
        }

        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            // A CAPI container name is already a bare file name in MachineKeys; a CNG UniqueName may
            // be either a bare name or a full path, depending on the provider.
            if (Path.IsPathRooted(name) && File.Exists(name))
            {
                return name;
            }

            // Machine locations first, because a service's certificate belongs in LocalMachine\My and
            // that is what this is for. The user ones are last so a developer with a personal
            // certificate gets an answer rather than a null.
            foreach (var directory in new[]
            {
                Path.Combine(common, "Microsoft", "Crypto", "Keys"),
                Path.Combine(common, "Microsoft", "Crypto", "RSA", "MachineKeys"),
                Path.Combine(common, "Microsoft", "Crypto", "SystemKeys"),
                Path.Combine(user, "Microsoft", "Crypto", "Keys"),
                Path.Combine(user, "Microsoft", "Crypto", "RSA"),
            })
            {
                var candidate = Path.Combine(directory, Path.GetFileName(name));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
