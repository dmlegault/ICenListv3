using System.Net;
using System.Text;

using Enlist.Installer.Detection;

namespace Enlist.Installer.Detection.Tests;

/// <summary>
/// The Verify button on the Control Plane and Agent pages, against a stand-in /health.
///
/// A handler rather than a real control plane, and deliberately: what is under test is how the page
/// READS an answer, and every interesting case is one a real control plane will not produce on
/// demand. A 503 with a healthy-looking body, something that answers but is not enList, a TLS
/// failure. Spawning a control plane would test the control plane; this tests the wizard.
/// </summary>
public sealed class ProbeTests
{
    private sealed class CannedResponse : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly Exception? _throw;

        public CannedResponse(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public CannedResponse(Exception toThrow)
        {
            _throw = toThrow;
            _body = "";
        }

        public Uri? Requested { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested = request.RequestUri;

            if (_throw is not null)
            {
                throw _throw;
            }

            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private const string HealthyBody =
        """{"status":"Healthy","product":"enList control plane","version":"3.0.0","database":{"reachable":true,"latestMigration":"20260911192322_InitialCreate","error":null},"checkedAtUtc":"2026-09-13T12:46:26Z","authentication":"Required"}""";

    [Fact]
    public async Task A_healthy_control_plane_is_reported_with_its_version_and_its_authentication_mode()
    {
        var handler = new CannedResponse(HttpStatusCode.OK, HealthyBody);

        var result = await Probes.VerifyControlPlaneAsync("https://enlist.corp.local:5293", handler);

        Assert.True(result.Ok, result.Message);
        Assert.Contains("3.0.0", result.Message);

        // Worth putting on the page: an operator who expected a locked-down control plane and sees
        // "authentication Off" has found a misconfiguration before installing anything against it.
        Assert.Contains("Required", result.Message);

        // /health, appended to whatever was typed - not the typed URL itself.
        Assert.Equal("/health", handler.Requested!.AbsolutePath);
    }

    [Fact]
    public async Task An_unhealthy_control_plane_is_told_apart_from_something_that_is_not_enList()
    {
        // 503 with a body that still names the product: the control plane is up, its database is not.
        // A different problem from "nothing there", and the remedy is different too.
        var unhealthy = """{"status":"Unhealthy","product":"enList control plane","version":"3.0.0","database":{"reachable":false,"error":"login failed"},"authentication":"Required"}""";

        var result = await Probes.VerifyControlPlaneAsync("https://enlist.corp.local:5293", new CannedResponse(HttpStatusCode.ServiceUnavailable, unhealthy));

        Assert.False(result.Ok);
        Assert.Contains("database", result.Message);
        Assert.DoesNotContain("not an enList", result.Message);
    }

    [Fact]
    public async Task Something_else_answering_on_that_port_is_not_mistaken_for_a_control_plane()
    {
        // The common mistake is pointing at the PORTAL, which answers 200 with HTML all day.
        var result = await Probes.VerifyControlPlaneAsync(
            "https://enlist.corp.local:5231",
            new CannedResponse(HttpStatusCode.OK, "<!DOCTYPE html><html><head><title>enList Portal</title></head></html>"));

        Assert.False(result.Ok);
        Assert.Contains("not an enList control plane", result.Message);
    }

    [Fact]
    public async Task A_certificate_failure_says_so_and_names_the_remedy()
    {
        // The failure every first TLS deployment meets. .NET's own message for it is "The SSL
        // connection could not be established, see inner exception", which names neither the cause
        // nor anything to do about it - the same defect fixed in the product on 2026-09-13.
        var tls = new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new System.Security.Authentication.AuthenticationException(
                "The remote certificate is invalid according to the validation procedure.",
                new Exception("The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot")));

        var result = await Probes.VerifyControlPlaneAsync("https://enlist.corp.local:5293", new CannedResponse(tls));

        Assert.False(result.Ok);
        Assert.Contains("UntrustedRoot", result.Message);
        Assert.Contains("trust store", result.Message);
        Assert.DoesNotContain("see inner exception", result.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("enlist.corp.local")]
    [InlineData("ftp://enlist.corp.local")]
    public async Task Something_that_is_not_an_http_url_is_refused_before_anything_is_dialled(string url)
    {
        var handler = new CannedResponse(HttpStatusCode.OK, HealthyBody);

        var result = await Probes.VerifyControlPlaneAsync(url, handler);

        Assert.False(result.Ok);
        Assert.Null(handler.Requested);
    }

    [Fact]
    public void A_windows_authentication_connection_string_carries_no_credential()
    {
        var connection = Probes.BuildConnectionString("sql01.corp.local", "EnlistControlPlane", windowsAuthentication: true, user: null, password: null);

        Assert.Contains("sql01.corp.local", connection);
        Assert.Contains("EnlistControlPlane", connection);
        Assert.Contains("Integrated Security=True", connection);
        Assert.DoesNotContain("Password", connection);
    }

    [Fact]
    public void A_sql_login_connection_string_carries_one_which_is_why_it_never_reaches_a_service_command_line()
    {
        var connection = Probes.BuildConnectionString("sql01.corp.local", "EnlistControlPlane", windowsAuthentication: false, user: "enlist", password: "hunter2");

        Assert.Contains("User ID=enlist", connection);
        Assert.Contains("hunter2", connection);

        // The MSI refuses to compose a connection string at all when DB_AUTH is not Windows, for
        // exactly this reason: a binPath is readable by any local user.
        Assert.DoesNotContain("Integrated Security=True", connection);
    }

    [Fact]
    public async Task A_database_test_against_nothing_fails_with_a_sentence_rather_than_a_stack_trace()
    {
        // Port 1 is not SQL Server on any machine, and the connect timeout keeps this quick.
        var connection = Probes.BuildConnectionString("127.0.0.1,1", "EnlistControlPlane", windowsAuthentication: true, user: null, password: null);

        var result = await Probes.TestDatabaseAsync(connection);

        Assert.False(result.Ok);
        Assert.DoesNotContain("   at ", result.Message);
        Assert.False(result.Message.Contains('\n'), "the message should be one sentence for a page, not a dump");
    }

    [Fact]
    public async Task An_empty_connection_string_asks_for_one_rather_than_failing_obscurely()
    {
        var result = await Probes.TestDatabaseAsync("");
        Assert.False(result.Ok);
        Assert.Contains("Enter", result.Message);
    }
}
