using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Configuration;

namespace Enlist.Portal.Services;

/// <summary>
/// The portal's own key to the control plane - an Operator API key named "portal" by convention,
/// created with the control plane's <c>create-api-key</c> verb at install time (Authentication-Design.md
/// 6.1). Read from <c>ControlPlane:ApiKey</c>, which may hold the key in the clear or, as the installer
/// writes it, DPAPI-protected at machine scope with the <c>dpapi:</c> prefix (<see cref="ProtectedSettings"/>).
/// It is a root-equivalent secret: the portal presents it on every call regardless of who is looking
/// at the page, because the portal has already decided (Windows groups) whether that person may.
///
/// Missing is allowed - a portal on loopback in front of a control plane that runs Off needs none -
/// and is said once at startup; a control plane that then answers 401 says the rest.
/// </summary>
public sealed class PortalCredential
{
    public const string SettingName = "ControlPlane:ApiKey";

    public PortalCredential(string? key)
    {
        Key = key;
    }

    public string? Key { get; }

    public bool HasKey => Key is not null;

    public static PortalCredential FromConfiguration(IConfiguration configuration)
    {
        var key = ProtectedSettings.Reveal(configuration[SettingName]);
        if (key is not null && !key.StartsWith("enlk_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{SettingName} does not hold a management API key (they start with enlk_). Create one on the control plane host with " +
                "'Enlist.ControlPlane.exe create-api-key --name portal --role Operator --expires never' and store it here, protected: 'Enlist.Portal.exe protect <key>'.");
        }

        return new PortalCredential(key);
    }
}

/// <summary>
/// A configuration value that must not sit in a file in the clear: "dpapi:&lt;base64&gt;", protected at
/// machine scope, which is what a service can decrypt and a copy of the file taken elsewhere cannot.
/// The ACL on the file is still the actual control (the same discipline as the agent's credential
/// file); this keeps the secret out of a casual read, a backup, a pasted config. <c>Enlist.Portal.exe
/// protect &lt;secret&gt;</c> is how the installer, or an operator, produces the value.
/// </summary>
public static class ProtectedSettings
{
    public const string Prefix = "dpapi:";

    /// <summary>The secret, whether the value was stored protected or in the clear; null for an empty value.</summary>
    public static string? Reveal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        if (!value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("A dpapi: setting can only be read on Windows.");
        }

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value[Prefix.Length..]), optionalEntropy: null, DataProtectionScope.LocalMachine));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new InvalidOperationException(
                $"A dpapi: setting could not be decrypted ({ex.Message}). It is protected to the machine that wrote it; run 'Enlist.Portal.exe protect <secret>' on this machine and store the result.");
        }
    }

    public static string Protect(string secret)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("A dpapi: setting can only be written on Windows.");
        }

        return Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), optionalEntropy: null, DataProtectionScope.LocalMachine));
    }

    public static bool IsVerb(string[] args) => args.Length > 0 && args[0].Equals("protect", StringComparison.OrdinalIgnoreCase);

    /// <summary>Enlist.Portal.exe protect &lt;secret&gt; - prints the value to put in configuration.</summary>
    public static int Run(string[] args)
    {
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.Error.WriteLine("Usage: Enlist.Portal.exe protect <secret>");
            Console.Error.WriteLine("Prints a dpapi: value for appsettings.json (ControlPlane:ApiKey), decryptable on this machine only.");
            return 2;
        }

        Console.WriteLine(Protect(args[1]));
        return 0;
    }
}
