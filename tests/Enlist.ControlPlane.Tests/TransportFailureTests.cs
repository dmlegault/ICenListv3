using System.ComponentModel;
using System.Net.Sockets;
using System.Security.Authentication;

using Enlist.ControlPlane.Contracts;

namespace Enlist.ControlPlane.Tests;

/// <summary>
/// What an operator is told when a call to the control plane fails.
///
/// These are the exception chains .NET actually produces, reproduced here rather than provoked over a
/// socket, because the shapes are the whole point and a unit test can cover all of them in
/// milliseconds. The shapes were taken from a real TLS rehearsal on 2026-09-13 - the messages quoted
/// below are verbatim from that run, not invented.
/// </summary>
public sealed class TransportFailureTests
{
    /// <summary>
    /// The exception chain a certificate failure produces: HttpRequestException whose own message is
    /// the useless one, wrapping AuthenticationException, wrapping the exception that actually knows.
    /// </summary>
    private static Exception CertificateChain(string reason) =>
        new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException(
                "The remote certificate is invalid according to the validation procedure.",
                new Win32Exception(reason)));

    [Fact]
    public void A_certificate_failure_names_the_reason_and_the_remedy_instead_of_an_inner_exception_nobody_can_see()
    {
        var described = TransportFailure.Describe(
            CertificateChain("The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot"));

        // The cause, from the innermost exception - the only one that says WHICH certificate problem
        // this is, and therefore the only one that tells an operator what to do next.
        Assert.Contains("UntrustedRoot", described);

        // The remedy, which no message had before.
        Assert.Contains("trust store", described);

        // And not the eleven words that sent people looking for something they could not see.
        Assert.DoesNotContain("see inner exception", described);
    }

    [Fact]
    public void A_name_mismatch_is_reported_as_itself_not_as_an_untrusted_root()
    {
        // Distinguishing these matters: an untrusted root is fixed by installing a CA, a name mismatch
        // by using the name the certificate was issued for. Both were the same sentence before.
        var described = TransportFailure.Describe(
            CertificateChain("The remote certificate is invalid because of errors in the certificate chain: RemoteCertificateNameMismatch"));

        Assert.Contains("RemoteCertificateNameMismatch", described);
        Assert.DoesNotContain("UntrustedRoot", described);
        Assert.Contains("must also match", described);
    }

    [Fact]
    public void An_ordinary_connection_failure_is_left_exactly_as_it_was()
    {
        // The common case, and the one this must not make worse: nothing is listening. It already read
        // plainly, so it comes back unchanged and says nothing about certificates.
        var described = TransportFailure.Describe(
            new HttpRequestException(
                "An error occurred while sending the request.",
                new SocketException(10061) { }));

        Assert.DoesNotContain("trust store", described);
        Assert.DoesNotContain("TLS certificate", described);
    }

    [Fact]
    public void An_exception_with_no_inner_exception_is_its_own_message()
    {
        Assert.Equal("Boom.", TransportFailure.Describe(new InvalidOperationException("Boom.")));
    }

    [Fact]
    public void A_certificate_failure_that_says_nothing_still_produces_a_sentence()
    {
        // Defensive: an AuthenticationException with an empty innermost message must not yield a
        // dangling "was rejected - ." The remedy is the useful half anyway.
        var described = TransportFailure.Describe(
            new HttpRequestException("outer", new AuthenticationException("")));

        Assert.Contains("without saying why", described);
        Assert.Contains("trust store", described);
    }
}
