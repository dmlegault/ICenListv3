using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

using Enlist.ControlPlane.Contracts;

using Microsoft.Extensions.Configuration;

namespace Enlist.ControlPlane.Tests;

/// <summary>
/// The certificate the control plane and the portal serve HTTPS with.
///
/// This is the third hard rule, created by the other two: authentication defaults to Required,
/// Required refuses plain HTTP off loopback, and both listen defaults are `+`. So a real deployment
/// serves HTTPS, and HTTPS with no certificate is a service that binds and then fails every
/// handshake while complaining about an endpoint.
/// </summary>
public sealed class ServerCertificateTests
{
    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    [Theory]
    [InlineData("A1B2C3", "A1B2C3")]
    [InlineData("a1b2c3", "A1B2C3")]
    [InlineData("a1 b2 c3", "A1B2C3")]
    [InlineData("  A1B2C3  ", "A1B2C3")]
    public void A_thumbprint_is_normalised_to_bare_uppercase_hex(string given, string expected)
    {
        Assert.Equal(expected, ServerCertificate.Normalize(given));
    }

    [Fact]
    public void The_invisible_character_certmgr_puts_in_front_of_a_thumbprint_is_removed()
    {
        // THIS IS THE ONE THAT BITES. certmgr's Details tab renders the thumbprint for bidirectional
        // text, so copying it yields a leading U+200E LEFT-TO-RIGHT MARK and a space between every
        // byte. Both survive a paste into a config file, neither is visible in one, and an exact
        // string match then reports the certificate missing while it sits in the store.
        const string asCopiedFromCertmgr = "\u200e1a 2b 3c 4d 5e 6f 70 81 92 a3 b4 c5 d6 e7 f8 09 1a 2b 3c 4d";

        Assert.Equal("1A2B3C4D5E6F708192A3B4C5D6E7F8091A2B3C4D", ServerCertificate.Normalize(asCopiedFromCertmgr));
    }

    [Theory]
    [InlineData("A1:B2:C3", "A1B2C3")]
    [InlineData("A1-B2-C3", "A1B2C3")]
    public void The_separators_some_tools_print_are_ignored_too(string given, string expected)
    {
        Assert.Equal(expected, ServerCertificate.Normalize(given));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("the one in the email")]
    [InlineData("A1B2C3.pfx")]
    [InlineData("use the wildcard cert")]
    public void Something_that_is_not_a_thumbprint_normalises_to_nothing(string? given)
    {
        // NOT "strip everything that is not hex". Nearly every English sentence contains a, b, c, d,
        // e and f, so that rule turns "the one in the email" into "EEEA" and then reports THAT as the
        // certificate which could not be found - a wrong answer wearing the costume of a right one.
        // Only whitespace, bidi marks and separators are noise; anything else means a different kind
        // of value was configured.
        Assert.Equal("", ServerCertificate.Normalize(given));
    }

    [Fact]
    public void Plain_http_needs_no_certificate()
    {
        // The demo: loopback, authentication Off, no TLS anywhere. Demanding a certificate here would
        // break the one configuration that is allowed not to have one.
        Assert.Null(ServerCertificate.Violation(new[] { "http://localhost:5293" }, Config(), "control plane", isDevelopment: false));
    }

    [Fact]
    public void Https_with_nothing_configured_is_refused_by_name()
    {
        var violation = ServerCertificate.Violation(new[] { "https://+:5293" }, Config(), "control plane", isDevelopment: false);

        Assert.NotNull(violation);
        Assert.Contains("https://+:5293", violation);
        Assert.Contains(ServerCertificate.ThumbprintSetting, violation);

        // And it says why http is not the way out of this, because that is the first thing anyone
        // will try and it produces a service that installs cleanly and never starts.
        Assert.Contains("Required refuses plain HTTP", violation);
    }

    [Fact]
    public void A_thumbprint_or_a_pfx_satisfies_the_rule()
    {
        Assert.Null(ServerCertificate.Violation(
            new[] { "https://+:5293" },
            Config((ServerCertificate.ThumbprintSetting, "A1B2")),
            "control plane", isDevelopment: false));

        Assert.Null(ServerCertificate.Violation(
            new[] { "https://+:5293" },
            Config((ServerCertificate.PathSetting, @"C:\certs\enlist.pfx")),
            "control plane", isDevelopment: false));
    }

    [Fact]
    public void Kestrels_own_certificate_configuration_is_left_alone()
    {
        // Anyone who already configured Kestrel directly keeps working. Reporting "no certificate" at
        // a host that demonstrably has one would be the worst kind of wrong.
        Assert.Null(ServerCertificate.Violation(
            new[] { "https://+:5293" },
            Config(("Kestrel:Certificates:Default:Path", @"C:\certs\enlist.pfx")),
            "portal", isDevelopment: false));
    }

    [Fact]
    public void Development_is_exempt_because_that_is_where_the_dotnet_dev_certificate_lives()
    {
        // Kestrel serves an https listener with the ASP.NET Core development certificate when nothing
        // is configured - what `dotnet dev-certs https` installs, and what every `dotnet run` has
        // relied on since 2.1. Enforcing the rule there would refuse the one environment in which
        // https already works with no configuration at all, and it is how the portal's own tests run.
        Assert.Null(ServerCertificate.Violation(new[] { "https://0.0.0.0:0" }, Config(), "portal", isDevelopment: true));

        // Outside Development there is no such certificate, so the same silence means a service that
        // binds and then fails every handshake.
        Assert.NotNull(ServerCertificate.Violation(new[] { "https://0.0.0.0:0" }, Config(), "portal", isDevelopment: false));
    }

    [Fact]
    public void Mixed_listeners_are_judged_by_the_https_ones()
    {
        // A host on loopback http AND a real https listener still needs a certificate for the second.
        var violation = ServerCertificate.Violation(
            new[] { "http://localhost:5000", "https://+:5293" },
            Config(),
            "control plane", isDevelopment: false);

        Assert.NotNull(violation);
        Assert.Contains("https://+:5293", violation);
        Assert.DoesNotContain("http://localhost:5000", violation);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void A_certificate_in_the_store_is_found_by_its_thumbprint_however_it_was_copied()
    {
        Assert.True(OperatingSystem.IsWindows());

        using var certificate = SelfSigned();
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(certificate);

        try
        {
            var found = ServerCertificate.FindByThumbprint(ServerCertificate.Normalize(certificate.Thumbprint));
            Assert.NotNull(found);
            Assert.Equal(certificate.Thumbprint, found!.Thumbprint);
            found.Dispose();

            // And the same certificate via the spelling a person would actually paste.
            var asPasted = "\u200e" + string.Join(" ", Enumerable.Range(0, certificate.Thumbprint!.Length / 2)
                .Select(i => certificate.Thumbprint.Substring(i * 2, 2).ToLowerInvariant()));

            using var viaConfiguration = ServerCertificate.Load(Config((ServerCertificate.ThumbprintSetting, asPasted)));
            Assert.NotNull(viaConfiguration);
            Assert.Equal(certificate.Thumbprint, viaConfiguration!.Thumbprint);
        }
        finally
        {
            store.Remove(certificate);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void A_thumbprint_that_matches_nothing_says_so_rather_than_serving_nothing()
    {
        Assert.True(OperatingSystem.IsWindows());

        // Returning null here would mean Kestrel failing later with a message about an endpoint,
        // which is a long way from "that certificate is not on this machine".
        var error = Assert.Throws<InvalidOperationException>(() =>
            ServerCertificate.Load(Config((ServerCertificate.ThumbprintSetting, new string('A', 40)))));

        Assert.Contains(new string('A', 40), error.Message);
        Assert.Contains("LocalMachine\\My", error.Message);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void A_pfx_path_that_does_not_exist_names_the_path()
    {
        Assert.True(OperatingSystem.IsWindows());

        var missing = Path.Combine(Path.GetTempPath(), "enlist-not-here-" + Guid.NewGuid().ToString("N") + ".pfx");
        var error = Assert.Throws<InvalidOperationException>(() =>
            ServerCertificate.Load(Config((ServerCertificate.PathSetting, missing))));

        Assert.Contains(missing, error.Message);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Nothing_configured_loads_nothing_without_complaining()
    {
        Assert.True(OperatingSystem.IsWindows());

        // Not every host serves TLS, and Kestrel's own configuration is still allowed to be the one
        // in charge. Silence here is correct; the refusal belongs to Violation.
        Assert.Null(ServerCertificate.Load(Config()));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void The_service_account_is_granted_read_on_the_private_key()
    {
        Assert.True(OperatingSystem.IsWindows());

        using var certificate = SelfSigned();

        // A certificate in the store is readable by anyone; its PRIVATE KEY is a separate file with an
        // ACL of its own, granted only to whoever imported it. A certificate installed by an
        // administrator and served by a service running as NETWORK SERVICE - the default for both
        // hosts - starts, binds, and then fails every handshake.
        var keyFile = WindowsSecrets.GrantPrivateKeyAccess(certificate, "NT AUTHORITY\\NetworkService");

        Assert.True(File.Exists(keyFile), "the private key file should have been located");

        var expected = new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null);
        var rules = new FileInfo(keyFile).GetAccessControl()
            .GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();

        Assert.Contains(rules, r => r.IdentityReference.Equals(expected) && r.AccessControlType == AccessControlType.Allow);

        // Read, not FullControl: serving TLS uses the key and never needs to change or delete it.
        var granted = rules.First(r => r.IdentityReference.Equals(expected));
        Assert.False(granted.FileSystemRights.HasFlag(FileSystemRights.WriteData));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void A_certificate_with_no_private_key_is_refused_with_the_reason()
    {
        Assert.True(OperatingSystem.IsWindows());

        using var withKey = SelfSigned();
        using var publicOnly = X509CertificateLoader.LoadCertificate(withKey.Export(X509ContentType.Cert));

        var error = Assert.Throws<InvalidOperationException>(() =>
            WindowsSecrets.GrantPrivateKeyAccess(publicOnly, "NT AUTHORITY\\NetworkService"));

        // The usual cause is a PFX imported without its private key, and the message says so - the
        // certificate looks perfectly present in the store either way.
        Assert.Contains("no private key", error.Message);
    }

    /// <summary>
    /// A throwaway certificate whose key is PERSISTED, which is what makes it have a key file to ACL
    /// at all. An in-memory key would pass every assertion about the certificate and have nothing on
    /// disk behind it.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=enlist-certificate-tests", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));

        const string password = "enlist-tests";
        return X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(X509ContentType.Pfx, password),
            password,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }
}
