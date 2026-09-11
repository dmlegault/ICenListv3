using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Enlist.Agent.Credentials;

/// <summary>
/// <c>&lt;data&gt;\credential</c>: the agent token, next to <c>Packages\</c>, <c>Runners\</c> and
/// <c>Logs\</c>, written with DPAPI at machine scope and an ACL of SYSTEM, Administrators and the
/// account this process runs as (Authentication-Design section 4.3). Machine scope because the agent
/// runs as a service, where there is no user profile for user scope to key on; and machine-scope DPAPI
/// is decryptable by any process on the machine, so the ACL is the actual control - closed (inheritance
/// off) rather than layered on whatever the data root happens to allow. Both are applied, every time.
///
/// The token is never on a command line: <c>sc qc</c> shows a service's arguments to anyone who can
/// query it. This file is the only place it lives on the agent.
///
/// Windows only, as the agent is. Every public method says so rather than failing inside DPAPI.
/// </summary>
public sealed class AgentCredentialStore
{
    public const string FileName = "credential";

    public AgentCredentialStore(string dataRoot)
    {
        Path = System.IO.Path.Combine(dataRoot, FileName);
    }

    public string Path { get; }

    /// <summary>The stored token, or null when this agent has never enrolled. A file that exists but cannot be read is an <see cref="AgentStartupException"/> that names the file and the fix - never a silent null that would look like "never enrolled".</summary>
    public string? Load()
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Agent credentials are protected with Windows DPAPI; the agent runs on Windows only.");
        }

        byte[] plain;
        try
        {
            plain = ProtectedData.Unprotect(File.ReadAllBytes(Path), optionalEntropy: null, DataProtectionScope.LocalMachine);
        }
        catch (CryptographicException ex)
        {
            throw new AgentStartupException(
                $"The credential file {Path} could not be decrypted ({ex.Message}). It is protected to the machine that wrote it, so a data directory " +
                "copied from another machine will not do. Delete the file and enroll this agent with a new join token (--join-token <token>).");
        }

        var token = Encoding.UTF8.GetString(plain);
        if (!token.StartsWith("enla_", StringComparison.Ordinal))
        {
            throw new AgentStartupException($"The credential file {Path} does not hold an agent token. Delete it and enroll this agent with a new join token (--join-token <token>).");
        }

        return token;
    }

    /// <summary>Replaces whatever is stored. The file is created with its final ACL rather than created and then restricted, so there is no instant at which it is readable by whoever can read the data root.</summary>
    public void Save(string token)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Agent credentials are protected with Windows DPAPI; the agent runs on Windows only.");
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        // An existing file keeps the ACL it was created with; a fresh create is the only way to be
        // certain of the one below.
        File.Delete(Path);

        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), optionalEntropy: null, DataProtectionScope.LocalMachine);

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var identity in Readers())
        {
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        using var stream = new FileInfo(Path).Create(FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.Synchronize, FileShare.None, 4096, FileOptions.None, security);
        stream.Write(protectedBytes);
    }

    public void Delete()
    {
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }

    /// <summary>SYSTEM, the local Administrators group, and whoever this process runs as - the service account, or the developer at a terminal. Duplicates (a service running as SYSTEM) merge.</summary>
    [SupportedOSPlatform("windows")]
    private static IEnumerable<SecurityIdentifier> Readers()
    {
        yield return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        yield return new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        using var current = WindowsIdentity.GetCurrent();
        if (current.User is { } user)
        {
            yield return user;
        }
    }
}
