using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;

namespace Enlist.Installer.Detection
{
    /// <summary>One certificate the operator could choose, and whether it would actually work.</summary>
    public sealed class CertificateChoice
    {
        public CertificateChoice(string thumbprint, string subject, DateTime notBefore, DateTime notAfter, bool hasPrivateKey)
            : this(thumbprint, subject, "", notBefore, notAfter, hasPrivateKey)
        {
        }

        public CertificateChoice(string thumbprint, string subject, string friendlyName, DateTime notBefore, DateTime notAfter, bool hasPrivateKey)
        {
            Thumbprint = thumbprint;
            Subject = subject;
            FriendlyName = friendlyName ?? "";
            NotBefore = notBefore;
            NotAfter = notAfter;
            HasPrivateKey = hasPrivateKey;
        }

        public string Thumbprint { get; }

        /// <summary>The CN alone where there is one, because a full distinguished name is unreadable in a list.</summary>
        public string Subject { get; }

        /// <summary>
        /// The friendly name, which is often the ONLY thing that tells two certificates apart.
        ///
        /// Not decoration. A developer machine routinely holds several certificates for CN=localhost -
        /// IIS Express installs one, Docker installs another, ASP.NET Core's dev certificate a third -
        /// and a list showing the subject alone offers "localhost" three times. The friendly name is
        /// what an administrator sets precisely so a human can pick the right one, and it is blank as
        /// often as not, so it is used WHEN PRESENT rather than relied upon.
        /// </summary>
        public string FriendlyName { get; }

        public DateTime NotBefore { get; }

        public DateTime NotAfter { get; }

        /// <summary>Without one it cannot serve TLS at all, however right it looks.</summary>
        public bool HasPrivateKey { get; }

        /// <summary>How soon an expiry is close enough to be worth saying out loud.</summary>
        public const int ExpiringSoonDays = 60;

        public bool IsUsable => Problem == null;

        /// <summary>
        /// Why this certificate would not serve, or null. Said in the list rather than discovered at
        /// first start: all three of these produce a service that installs and then fails, and two of
        /// them are invisible in a subject line.
        /// </summary>
        public string? Problem
        {
            get
            {
                var now = DateTime.Now;
                if (!HasPrivateKey)
                {
                    return "no private key";
                }

                if (now > NotAfter)
                {
                    return "expired " + NotAfter.ToString("d");
                }

                return now < NotBefore ? "not valid until " + NotBefore.ToString("d") : null;
            }
        }

        /// <summary>
        /// A certificate that WILL serve and should probably not be chosen, or null.
        ///
        /// Separate from Problem on purpose: this one works, so it must not be refused. But a store
        /// routinely holds two certificates that are identical in every visible column - same subject,
        /// same friendly name, same issuer - differing only in that one expires shortly. Picking that
        /// one produces an install that is perfect today and fails TLS a few weeks later, long after
        /// anybody connects the two events. This machine has exactly that pair.
        /// </summary>
        public string? Caution
        {
            get
            {
                if (Problem != null)
                {
                    return null;
                }

                var remaining = NotAfter - DateTime.Now;
                if (remaining.TotalDays > ExpiringSoonDays)
                {
                    return null;
                }

                // Ceiling, not floor: a certificate valid until this time in twenty days is 19.99 days
                // away, and reporting that as nineteen is the kind of small wrongness that makes a
                // reader distrust the rest of the page.
                var days = (int)Math.Ceiling(remaining.TotalDays);
                return "expires in " + (remaining.TotalDays < 1 ? "less than a day" : days == 1 ? "1 day" : days + " days");
            }
        }

        /// <summary>
        /// One line an operator can actually choose between: the friendly name where there is one,
        /// the subject, the expiry, and the last eight of the thumbprint.
        ///
        /// The thumbprint tail is always there, and it is the part that makes this list correct
        /// rather than merely tidy. Two certificates can share a subject AND have no friendly name -
        /// nothing else in the row would differ, and picking the wrong one of those is a mistake that
        /// only shows up as an expired certificate being served weeks later. It is also what an
        /// operator cross-checks against certmgr, which shows the same digits.
        /// </summary>
        public string Display
        {
            get
            {
                var name = string.IsNullOrWhiteSpace(FriendlyName)
                    ? Subject
                    : FriendlyName + (string.IsNullOrWhiteSpace(Subject) || Subject == FriendlyName ? "" : " (" + Subject + ")");

                var tail = Thumbprint.Length >= 8 ? Thumbprint.Substring(Thumbprint.Length - 8) : Thumbprint;

                var note = Problem ?? Caution;

                return name + "  -  expires " + NotAfter.ToString("d") + "  -  ..." + tail +
                    (note == null ? "" : "  (" + note + ")");
            }
        }

        public override string ToString() => Display;
    }

    /// <summary>
    /// The certificates this machine could serve HTTPS with - LocalMachine\My, which is where a
    /// service's certificate belongs.
    ///
    /// The wizard offers these rather than asking for a thumbprint to be typed, because a thumbprint
    /// is forty hex characters that certmgr renders with spaces and an invisible left-to-right mark.
    /// Typing one correctly is possible; pasting one correctly is the thing that fails.
    /// </summary>
    public static class CertificateDetection
    {
        /// <summary>
        /// Everything in the machine's personal store, newest expiry first, unusable ones INCLUDED.
        ///
        /// Included deliberately: an operator looking for the certificate they just installed and not
        /// finding it in the list has no way to tell whether the list is wrong or the import was. It
        /// is shown with the reason it cannot be used instead.
        /// </summary>
        public static IReadOnlyList<CertificateChoice> Available()
        {
            var found = new List<CertificateChoice>();

            using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
            {
                try
                {
                    store.Open(OpenFlags.ReadOnly);
                }
                catch (Exception)
                {
                    // A store that will not open is an empty list, not a failed install. The operator
                    // can still type a thumbprint.
                    return found;
                }

                foreach (var certificate in store.Certificates)
                {
                    found.Add(new CertificateChoice(
                        certificate.Thumbprint ?? "",
                        FriendlySubject(certificate),
                        certificate.FriendlyName ?? "",
                        certificate.NotBefore,
                        certificate.NotAfter,
                        certificate.HasPrivateKey));
                }
            }

            return found
                .Where(c => c.Thumbprint.Length > 0)
                .OrderByDescending(c => c.IsUsable)
                .ThenByDescending(c => c.NotAfter)
                .ToList();
        }

        /// <summary>
        /// The common name, or the whole subject when there is no CN to pull out.
        ///
        /// A distinguished name in a dropdown is unreadable, and the part that identifies the
        /// certificate to a person is almost always the CN.
        /// </summary>
        public static string FriendlySubject(X509Certificate2 certificate)
        {
            var common = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            if (!string.IsNullOrWhiteSpace(common))
            {
                return common;
            }

            return string.IsNullOrWhiteSpace(certificate.Subject) ? certificate.Thumbprint ?? "" : certificate.Subject;
        }

        /// <summary>
        /// A thumbprint as it should be stored: bare uppercase hex, whatever spacing or invisible
        /// characters it arrived with.
        ///
        /// The same rule the hosts apply when they read it back (Enlist.ControlPlane.Contracts
        /// ServerCertificate.Normalize), restated here rather than shared because this assembly is
        /// net472/netstandard2.0 and runs before .NET 10 exists. The two must agree; the tests on both
        /// sides use the same certmgr paste to say so.
        /// </summary>
        public static string NormalizeThumbprint(string thumbprint)
        {
            if (string.IsNullOrWhiteSpace(thumbprint))
            {
                return "";
            }

            var cleaned = new System.Text.StringBuilder(thumbprint.Length);
            foreach (var character in thumbprint)
            {
                if (Uri.IsHexDigit(character))
                {
                    cleaned.Append(char.ToUpperInvariant(character));
                    continue;
                }

                if (char.IsWhiteSpace(character) || character == '‎' || character == '‏' ||
                    character == '‪' || character == '‬' || character == ':' || character == '-')
                {
                    continue;
                }

                return "";
            }

            return cleaned.ToString();
        }
    }
}
