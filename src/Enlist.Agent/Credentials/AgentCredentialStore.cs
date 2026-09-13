using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

using Enlist.ControlPlane.Contracts;

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
        : this(dataRoot, null)
    {
    }

    /// <summary>
    /// <paramref name="alsoReadableBy"/> exists for ONE caller: the installer, enrolling this agent
    /// before its service has ever run.
    ///
    /// The readers below are SYSTEM, Administrators, and whoever is writing the file. That is right
    /// when the agent enrols itself, because the writer IS the service account. It is wrong when the
    /// installer does it: the writer is the elevated account running setup, and a service configured
    /// to run as anything other than LocalSystem or an administrator would then find a credential
    /// file it cannot open - an install that looks clean and a service that will not start.
    ///
    /// So the account the service will run as is granted explicitly at enrollment time. Null, or one
    /// of the built-in accounts already covered, adds nothing.
    /// </summary>
    public AgentCredentialStore(string dataRoot, string? alsoReadableBy)
    {
        Path = System.IO.Path.Combine(dataRoot, FileName);
        AlsoReadableBy = string.IsNullOrWhiteSpace(alsoReadableBy) ? null : alsoReadableBy.Trim();
    }

    public string Path { get; }

    private string? AlsoReadableBy { get; }

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

        var security = WindowsSecrets.ClosedTo(Readers());

        using var stream = new FileInfo(Path).Create(FileMode.CreateNew, System.Security.AccessControl.FileSystemRights.Write | System.Security.AccessControl.FileSystemRights.Synchronize, FileShare.None, 4096, FileOptions.None, security);
        stream.Write(protectedBytes);
    }

    /// <summary>
    /// Removes the stored credential. Nothing in the agent calls this, and that is deliberate rather
    /// than an oversight: every failure path above tells the OPERATOR to delete the file, because
    /// each one means the credential does not belong to this machine or this agent, and quietly
    /// deleting a credential the agent cannot read is how you turn a diagnosable problem into a
    /// silent re-enrollment loop.
    ///
    /// Kept because the installer will need it - an uninstall that leaves a live credential behind is
    /// the same trap .demo/README.md now warns about for the demo's own data directories.
    /// </summary>
    public void Delete()
    {
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }

    /// <summary>
    /// SYSTEM, the local Administrators group, whoever this process runs as - the service account, or
    /// the developer at a terminal - and, when the installer says so, the account the service is
    /// about to run as. Duplicates (a service running as SYSTEM) merge.
    ///
    /// Shared with the portal, which writes its own secret and meets the same trap: the built-in
    /// service accounts are spelled in ways the account database does not recognise.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private IEnumerable<SecurityIdentifier> Readers()
    {
        // Wrapped so an unresolvable account reads as a reason the AGENT will not start, which is
        // what every other failure in this class reads as.
        try
        {
            return WindowsSecrets.Readers(AlsoReadableBy).ToList();
        }
        catch (InvalidOperationException ex)
        {
            throw new AgentStartupException(ex.Message);
        }
    }
}
