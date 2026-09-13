using System.Security.Authentication;

namespace Enlist.ControlPlane.Contracts;

/// <summary>
/// Turns a failed call to the control plane into a sentence an operator can act on.
///
/// Written 2026-09-13, after a TLS rehearsal - the first time anything here had been run against real
/// certificates rather than loopback HTTP. Every client reported a TLS failure by printing
/// <c>ex.Message</c>, and for a certificate problem .NET's message is always the same eleven words:
/// "The SSL connection could not be established, see inner exception." The actual reason lives in the
/// INNER exception, which nothing ever printed. So the agent, the deploy CLI and the portal all told
/// an operator to consult information none of them showed them:
///
///   enlist-agent not starting: Could not reach the control plane at https://kensho:7443/ to enroll:
///   The SSL connection could not be established, see inner exception.
///
/// That is the first message anybody sees on the first TLS deployment, and it names no cause, no
/// remedy, and nothing to distinguish an untrusted certificate from a wrong port or a stopped
/// service. It is also the message after a certificate rotation, on an agent that was working
/// yesterday.
///
/// This walks the chain to the exception that actually knows, and adds the remedy for the one case
/// that is nearly always the answer: the certificate is fine and the machine has simply never been
/// told to trust whoever issued it.
/// </summary>
public static class TransportFailure
{
    /// <summary>
    /// A one-line description of why a call failed, with the remedy when the cause is a certificate.
    /// Safe for any exception: an ordinary failure comes back as its own message, unchanged.
    /// </summary>
    public static string Describe(Exception exception)
    {
        var certificate = FindCertificateFailure(exception);
        if (certificate is null)
        {
            return Innermost(exception).Message;
        }

        return $"the server's TLS certificate was rejected - {certificate}. " +
            "Either install the issuing CA in this machine's trust store (Local Machine > Trusted Root " +
            "Certification Authorities), or serve a certificate from a CA it already trusts. The name in " +
            "the URL must also match the certificate's subject or one of its SANs.";
    }

    /// <summary>
    /// The reason a TLS handshake failed, or null when the failure was not about the certificate.
    ///
    /// Found by walking to the innermost exception rather than by matching on the top-level message:
    /// HttpRequestException wraps AuthenticationException wraps (on Windows) a Win32Exception whose
    /// message is the only one that names the actual fault - an untrusted root, an expired
    /// certificate, a name mismatch. Which of those it is decides what an operator does next, so it
    /// is worth more than everything above it in the chain put together.
    /// </summary>
    private static string? FindCertificateFailure(Exception exception)
    {
        var sawAuthenticationFailure = false;
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException)
            {
                sawAuthenticationFailure = true;
            }
        }

        if (!sawAuthenticationFailure)
        {
            return null;
        }

        var reason = Innermost(exception).Message.Trim().TrimEnd('.');
        return string.IsNullOrEmpty(reason) ? "the handshake failed without saying why" : reason;
    }

    private static Exception Innermost(Exception exception)
    {
        var current = exception;
        while (current.InnerException is { } inner)
        {
            current = inner;
        }

        return current;
    }
}
