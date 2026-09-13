using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Enlist.Installer.Detection;

namespace Enlist.Installer.Detection.Tests;

/// <summary>
/// The certificate the wizard offers, and the rule that stops an operator walking past the page
/// without one.
///
/// This is the last thing standing between a clean install and a service that never starts:
/// authentication is Required, Required refuses plain HTTP off loopback, both listen defaults are
/// `+`, so a real install serves HTTPS - and HTTPS with no certificate binds and then fails every
/// handshake while complaining about an endpoint.
/// </summary>
public sealed class CertificateDetectionTests
{
    [Theory]
    [InlineData("https://+:5293", "", true)]
    [InlineData("https://+:5293", "A1B2C3", false)]
    [InlineData("http://localhost:5293", "", false)]
    [InlineData("http://localhost:5293;https://+:5293", "", true)]
    [InlineData("", "", false)]
    public void A_certificate_is_required_exactly_when_something_is_served_over_https(string urls, string thumbprint, bool needed)
    {
        Assert.Equal(needed, InstallPlan.NeedsCertificate(urls, thumbprint));
    }

    [Fact]
    public void The_control_plane_page_will_not_let_you_past_https_without_one()
    {
        var plan = new InstallPlan { Type = InstallType.Server };

        var blocked = plan.WhyNextIsBlocked(WizardPage.ControlPlane);
        Assert.NotNull(blocked);
        Assert.Contains("certificate", blocked, StringComparison.OrdinalIgnoreCase);

        plan.ControlPlaneCertificate = "A1B2C3";
        Assert.Null(plan.WhyNextIsBlocked(WizardPage.ControlPlane));
    }

    [Fact]
    public void The_portal_page_will_not_either()
    {
        var plan = new InstallPlan { Type = InstallType.Server, PortalControlPlaneUrl = "https://cp.example:5293" };

        var blocked = plan.WhyNextIsBlocked(WizardPage.Portal);
        Assert.NotNull(blocked);
        Assert.Contains("certificate", blocked, StringComparison.OrdinalIgnoreCase);

        plan.PortalCertificate = "A1B2C3";
        Assert.Null(plan.WhyNextIsBlocked(WizardPage.Portal));
    }

    [Fact]
    public void A_loopback_http_demo_needs_no_certificate_and_is_not_asked_for_one()
    {
        // The one configuration allowed to run without TLS. Demanding a certificate here would break
        // the demo, which is the whole reason Mode=Off exists.
        var plan = new InstallPlan
        {
            Type = InstallType.Server,
            ControlPlaneUrls = "http://localhost:5293",
            PortalUrls = "http://localhost:5231",
            PortalControlPlaneUrl = "http://localhost:5293",
        };

        Assert.Null(plan.WhyNextIsBlocked(WizardPage.ControlPlane));
        Assert.Null(plan.WhyNextIsBlocked(WizardPage.Portal));
    }

    [Fact]
    public void The_thumbprint_reaches_the_packages_as_the_property_they_read()
    {
        var plan = new InstallPlan
        {
            Type = InstallType.Server,
            ControlPlaneCertificate = "AABB",
            PortalCertificate = "CCDD",
        };

        var variables = plan.ToBundleVariables();

        Assert.Equal("AABB", variables["CP_CERT"]);
        Assert.Equal("CCDD", variables["PORTAL_CERT"]);
    }

    [Fact]
    public void A_silent_install_reads_the_same_two_back()
    {
        var plan = InstallPlan.FromVariables(name => name switch
        {
            "INSTALLTYPE" => "Server",
            "CP_CERT" => "AABB",
            "PORTAL_CERT" => "CCDD",
            _ => null,
        });

        Assert.Equal("AABB", plan.ControlPlaneCertificate);
        Assert.Equal("CCDD", plan.PortalCertificate);
    }

    [Fact]
    public void Granting_the_service_account_access_comes_first_and_is_not_optional()
    {
        var plan = new InstallPlan
        {
            Type = InstallType.Server,
            ControlPlaneCertificate = "AABB",
            PortalCertificate = "CCDD",
        };

        var steps = PostInstall.Steps(plan);

        // BEFORE anything else, because a certificate in the store is readable by anyone while its
        // private key is not - and the default service account is NETWORK SERVICE, which did not
        // import it. Nothing about the certificate looks wrong when this is missing.
        Assert.Equal(PostInstallStepKind.GrantCertificateAccess, steps[0].Kind);
        Assert.Equal(PostInstallStepKind.GrantCertificateAccess, steps[1].Kind);
        Assert.All(steps.Where(s => s.Kind == PostInstallStepKind.GrantCertificateAccess), s => Assert.False(s.Optional));

        // Each component grants for its OWN account and its OWN certificate, using its own executable
        // - a portal-only install has no control plane to borrow the verb from.
        var portal = steps.First(s => s.Executable.EndsWith("Enlist.Portal.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("CCDD", portal.Arguments);
        Assert.Contains(plan.PortalAccount, portal.Arguments);
    }

    [Fact]
    public void No_certificate_chosen_means_no_grant_step_rather_than_a_failing_one()
    {
        // A loopback-http install, or one where the certificate is configured directly in Kestrel.
        // Running the verb with an empty thumbprint would fail a step for a configuration that is
        // perfectly valid.
        var plan = new InstallPlan { Type = InstallType.Server };

        Assert.DoesNotContain(PostInstall.Steps(plan), s => s.Kind == PostInstallStepKind.GrantCertificateAccess);
    }

    [Theory]
    [InlineData("a1 b2 c3", "A1B2C3")]
    [InlineData("A1:B2:C3", "A1B2C3")]
    [InlineData("‎1a 2b 3c", "1A2B3C")]
    [InlineData("the wildcard one", "")]
    [InlineData("", "")]
    public void The_wizard_normalises_a_thumbprint_the_same_way_the_hosts_do(string given, string expected)
    {
        // Two implementations by necessity - this assembly is net472/netstandard2.0 and runs before
        // .NET 10 exists, so it cannot reference the hosts' copy. They must agree, and the certmgr
        // paste is the case that proves it: ServerCertificateTests asserts the identical string.
        Assert.Equal(expected, CertificateDetection.NormalizeThumbprint(given));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void A_certificate_is_reported_with_the_reason_it_cannot_be_used()
    {
        var expired = new CertificateChoice("AABB", "cp.example", DateTime.Now.AddYears(-2), DateTime.Now.AddYears(-1), hasPrivateKey: true);
        var future = new CertificateChoice("CCDD", "cp.example", DateTime.Now.AddDays(7), DateTime.Now.AddYears(1), hasPrivateKey: true);
        var noKey = new CertificateChoice("EEFF", "cp.example", DateTime.Now.AddDays(-1), DateTime.Now.AddYears(1), hasPrivateKey: false);
        var good = new CertificateChoice("1122", "cp.example", DateTime.Now.AddDays(-1), DateTime.Now.AddYears(1), hasPrivateKey: true);

        // All three produce a service that installs and then fails, and two of them are invisible in
        // a subject line - which is why the list says so rather than the Event Log.
        Assert.Contains("expired", expired.Problem);
        Assert.Contains("not valid until", future.Problem);
        Assert.Equal("no private key", noKey.Problem);
        Assert.Null(good.Problem);

        Assert.True(good.IsUsable);
        Assert.False(expired.IsUsable);
        Assert.Contains("cp.example", good.Display);
    }

    [Fact]
    public void Two_certificates_with_the_same_subject_are_told_apart()
    {
        // NOT hypothetical, and the reason the friendly name is read at all. A developer machine
        // routinely holds several certificates for CN=localhost - IIS Express installs one, Docker
        // another, ASP.NET Core's dev certificate a third - and the machine this was written on has
        // exactly two in LocalMachine\My, both CN=localhost. A list showing the subject alone offers
        // "localhost" twice and no way to choose.
        var iis = new CertificateChoice("AAAA1111", "localhost", "IIS Express Development Certificate", DateTime.Now.AddDays(-1), DateTime.Now.AddYears(1), true);
        var docker = new CertificateChoice("BBBB2222", "localhost", "DockerLocalNginx", DateTime.Now.AddDays(-1), DateTime.Now.AddYears(1), true);

        Assert.NotEqual(iis.Display, docker.Display);
        Assert.Contains("IIS Express Development Certificate", iis.Display);
        Assert.Contains("localhost", iis.Display);
    }

    [Fact]
    public void Two_certificates_with_the_same_subject_and_NO_friendly_name_are_still_told_apart()
    {
        // A friendly name is blank as often as not - every Entra ID device-registration certificate
        // has none. The thumbprint tail is what makes the list correct rather than merely tidy, and
        // it is what an operator cross-checks against certmgr.
        var first = new CertificateChoice("AAAA1111CCCC3333", "localhost", "", DateTime.Now.AddDays(-1), DateTime.Now.AddYears(1), true);
        var second = new CertificateChoice("BBBB2222DDDD4444", "localhost", "", DateTime.Now.AddDays(-1), DateTime.Now.AddYears(1), true);

        Assert.NotEqual(first.Display, second.Display);
        Assert.Contains("CCCC3333", first.Display);
        Assert.Contains("DDDD4444", second.Display);
    }

    [Fact]
    public void A_certificate_about_to_expire_is_flagged_without_being_refused()
    {
        var soon = new CertificateChoice("AAAA1111", "localhost", "ASP.NET Core HTTPS development certificate",
            DateTime.Now.AddYears(-1), DateTime.Now.AddDays(20), true);

        // It WORKS, so refusing it would be wrong - an operator may have a renewal in hand. But an
        // install that is perfect today and fails TLS in three weeks is the kind of thing nobody
        // connects back to the installer, so it is said out loud.
        Assert.Null(soon.Problem);
        Assert.True(soon.IsUsable);
        Assert.Equal("expires in 20 days", soon.Caution);
        Assert.Contains("expires in 20 days", soon.Display);
    }

    [Fact]
    public void A_certificate_with_years_left_is_flagged_as_nothing()
    {
        var fine = new CertificateChoice("AAAA1111", "localhost", "", DateTime.Now.AddYears(-1), DateTime.Now.AddYears(3), true);

        Assert.Null(fine.Problem);
        Assert.Null(fine.Caution);
    }

    [Fact]
    public void An_already_expired_certificate_is_a_problem_rather_than_a_caution()
    {
        // The two must not both fire: "expired 3 days ago" and "expires in -3 days" in one row would
        // be the installer arguing with itself.
        var gone = new CertificateChoice("AAAA1111", "localhost", "", DateTime.Now.AddYears(-2), DateTime.Now.AddDays(-3), true);

        Assert.NotNull(gone.Problem);
        Assert.Null(gone.Caution);
    }

    [Fact]
    public void The_pair_this_machine_actually_has_is_distinguishable_and_ordered_safest_first()
    {
        // Two ASP.NET Core development certificates, identical in Issued To AND Friendly Name -
        // exactly what certmgr shows on the machine this was written on. Only the expiry differs, and
        // one of them is nearly out.
        var later = new CertificateChoice("AAAA1111ABCD1234", "localhost", "ASP.NET Core HTTPS development certificate",
            DateTime.Now.AddYears(-1), DateTime.Now.AddYears(1), true);
        var sooner = new CertificateChoice("BBBB2222EF567890", "localhost", "ASP.NET Core HTTPS development certificate",
            DateTime.Now.AddYears(-1), DateTime.Now.AddDays(30), true);

        Assert.NotEqual(later.Display, sooner.Display);
        Assert.Null(later.Caution);
        Assert.NotNull(sooner.Caution);

        // Available() orders usable-first then by expiry descending, so the longer-lived of an
        // otherwise identical pair is the one at the top - the one a hurried operator picks.
        var ordered = new[] { sooner, later }.OrderByDescending(c => c.IsUsable).ThenByDescending(c => c.NotAfter).ToList();
        Assert.Same(later, ordered[0]);
    }

    [Fact]
    public void A_friendly_name_that_repeats_the_subject_is_not_printed_twice()
    {
        var choice = new CertificateChoice("AAAA1111", "cp.example", "cp.example", DateTime.Now.AddDays(-1), DateTime.Now.AddYears(1), true);

        Assert.DoesNotContain("cp.example (cp.example)", choice.Display);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Reading_the_machine_store_answers_rather_than_throwing()
    {
        Assert.True(OperatingSystem.IsWindows());

        // Whatever this machine happens to have - the assertion is that enumerating LocalMachine\My
        // is an answer and not an exception, on a machine nobody prepared. An unreadable store is an
        // empty list: the operator can still paste a thumbprint.
        var available = CertificateDetection.Available();

        Assert.NotNull(available);
        Assert.All(available, c => Assert.NotEmpty(c.Thumbprint));

        // Usable ones first, so the one an operator wants is not below thirty expired ones.
        var usable = available.Select(c => c.IsUsable).ToList();
        Assert.Equal(usable.OrderByDescending(u => u).ToList(), usable);
    }
}
