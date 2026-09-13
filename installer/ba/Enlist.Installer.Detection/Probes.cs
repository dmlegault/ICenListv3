using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using System.Data.SqlClient;

namespace Enlist.Installer.Detection
{
    /// <summary>The answer a Verify or Test button puts on screen.</summary>
    public sealed class ProbeResult
    {
        private ProbeResult(bool ok, string message)
        {
            Ok = ok;
            Message = message;
        }

        public bool Ok { get; }

        /// <summary>One sentence, written for the person looking at the page, never an exception dump.</summary>
        public string Message { get; }

        public static ProbeResult Good(string message) => new ProbeResult(true, message);

        public static ProbeResult Bad(string message) => new ProbeResult(false, message);

        public override string ToString() => (Ok ? "ok: " : "failed: ") + Message;
    }

    /// <summary>
    /// The two things the wizard can prove before it installs anything: that the control plane URL
    /// someone typed answers, and that the database connection they described works.
    ///
    /// Both exist because the alternative is finding out at first start, from a service that will not
    /// run, with the reason in an Event Log entry. The design gives both a button and a spinner for
    /// exactly that reason, and silent mode runs them too - failing the install unless
    /// AGENT_CPURL_SKIPVERIFY or DB_SKIPTEST is passed explicitly.
    /// </summary>
    public static class Probes
    {
        /// <summary>Five seconds, as section 6.7 specifies. Long enough for a LAN, short enough to stay a UI interaction.</summary>
        public static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// GET {url}/health, and read the answer rather than just the status code.
        ///
        /// /health is the only anonymous endpoint, which is what makes it usable here: the wizard has
        /// no credential yet and will not have one until enrollment. It also reports whether
        /// authentication is Required, and saying so on the page is worth more than it looks - an
        /// operator who sees "authentication: Off" on a machine they expected to be locked down has
        /// found a misconfiguration before installing anything against it.
        /// </summary>
        public static async Task<ProbeResult> VerifyControlPlaneAsync(string url, HttpMessageHandler? handler = null)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return ProbeResult.Bad("Enter the control plane's URL first.");
            }

            Uri parsed;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                return ProbeResult.Bad("That is not an http or https URL.");
            }

            var client = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            try
            {
                client.Timeout = VerifyTimeout;
                var health = new Uri(parsed, "/health");

                using (var response = await client.GetAsync(health).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    // A 503 still names the product: the control plane is up and its database is not,
                    // which is a different problem from "this is not enList" and is worth saying.
                    if (body.IndexOf("enList control plane", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        return ProbeResult.Bad(
                            "Something answered at " + health + " but it is not an enList control plane.");
                    }

                    var authentication = ReadJsonString(body, "authentication");
                    var suffix = string.IsNullOrEmpty(authentication) ? "" : ", authentication " + authentication;

                    if (!response.IsSuccessStatusCode)
                    {
                        return ProbeResult.Bad(
                            "The control plane answered, but reports itself unhealthy (its database is not reachable)" + suffix + ".");
                    }

                    var version = ReadJsonString(body, "version");
                    return ProbeResult.Good(
                        "reachable - enList control plane" + (string.IsNullOrEmpty(version) ? "" : " " + version) + suffix);
                }
            }
            catch (TaskCanceledException)
            {
                return ProbeResult.Bad("No answer within " + (int)VerifyTimeout.TotalSeconds + " seconds. Check the address, the port and any firewall.");
            }
            catch (Exception ex)
            {
                // TLS is the failure worth naming, because it is the one every first real deployment
                // meets and the one whose own message says nothing. Same reasoning, and the same
                // remedy, as Enlist.ControlPlane.Contracts.TransportFailure.
                return ProbeResult.Bad(DescribeTransportFailure(ex));
            }
            finally
            {
                client.Dispose();
            }
        }

        /// <summary>
        /// Opens the connection the Database page describes, and nothing else. It does not create a
        /// database, apply migrations or read a table: those are later steps that must be allowed to
        /// fail on their own terms.
        /// </summary>
        public static async Task<ProbeResult> TestDatabaseAsync(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return ProbeResult.Bad("Enter a server and a database first.");
            }

            try
            {
                using (var connection = new SqlConnection(connectionString))
                {
                    await connection.OpenAsync().ConfigureAwait(false);
                    return ProbeResult.Good("connected to " + connection.DataSource + ", " + connection.ServerVersion);
                }
            }
            catch (SqlException ex)
            {
                // SQL Server's own message is usually the useful one, and it already names the server.
                return ProbeResult.Bad(FirstSentence(ex.Message));
            }
            catch (Exception ex)
            {
                return ProbeResult.Bad(FirstSentence(ex.Message));
            }
        }

        /// <summary>
        /// The connection string the control plane will be given, built from what the page collected.
        /// Windows authentication carries no secret; a SQL login does, which is why the MSI refuses to
        /// put one on a service command line and the bootstrapper writes it to an ACL-restricted
        /// appsettings.json instead.
        /// </summary>
        public static string BuildConnectionString(string server, string database, bool windowsAuthentication, string? user, string? password)
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = server ?? "",
                InitialCatalog = database ?? "",
                TrustServerCertificate = true,
                ConnectTimeout = (int)VerifyTimeout.TotalSeconds,
            };

            if (windowsAuthentication)
            {
                builder.IntegratedSecurity = true;
            }
            else
            {
                builder.UserID = user ?? "";
                builder.Password = password ?? "";
            }

            return builder.ConnectionString;
        }

        private static string DescribeTransportFailure(Exception exception)
        {
            for (var current = exception; current != null; current = current.InnerException)
            {
                if (current is System.Security.Authentication.AuthenticationException)
                {
                    var innermost = exception;
                    while (innermost.InnerException != null)
                    {
                        innermost = innermost.InnerException;
                    }

                    return "The server's TLS certificate was rejected - " + innermost.Message.TrimEnd('.') +
                        ". Install the issuing CA in this machine's trust store, or use a certificate from one it already trusts.";
                }
            }

            var last = exception;
            while (last.InnerException != null)
            {
                last = last.InnerException;
            }

            return FirstSentence(last.Message);
        }

        private static string FirstSentence(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return "It failed without saying why.";
            }

            var trimmed = message.Trim();
            var stop = trimmed.IndexOf(". ", StringComparison.Ordinal);
            return stop < 0 ? trimmed : trimmed.Substring(0, stop + 1);
        }

        /// <summary>
        /// Reads one string property out of /health's JSON without taking a JSON dependency into the
        /// bootstrapper, which runs before .NET 10 exists and has to stay small. The shape is fixed
        /// and this reads exactly two fields from it; anything more would deserve a real parser.
        /// </summary>
        private static string ReadJsonString(string json, string property)
        {
            var marker = "\"" + property + "\"";
            var at = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return "";
            }

            var colon = json.IndexOf(':', at + marker.Length);
            if (colon < 0)
            {
                return "";
            }

            var open = json.IndexOf('"', colon + 1);
            if (open < 0)
            {
                return "";
            }

            var close = json.IndexOf('"', open + 1);
            return close < 0 ? "" : json.Substring(open + 1, close - open - 1);
        }
    }
}
