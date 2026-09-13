using System.Runtime.Versioning;
using System.Security.AccessControl;
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
}
