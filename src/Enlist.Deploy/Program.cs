using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Enlist.ControlPlane.Contracts;

namespace Enlist.Deploy;

/// <summary>
/// enlist-deploy --control-plane &lt;url&gt; --app &lt;name&gt; --source &lt;dir&gt; [--api-key &lt;key&gt;]
///
/// Zips --source and uploads it once (content-addressed — re-running this against an unchanged build
/// is a no-op upload, see PackageBlobStore). That is the entire job of this tool: it publishes a
/// package, nothing more. Deciding WHERE an application runs — which agents qualify, whether it's
/// enabled, cron overrides — is an exclusively-portal decision made afterward, on the Applications tab.
/// A freshly-uploaded package with no Application Policy yet is an expected, normal state, not an
/// error — it will simply show up in the portal's Applications catalog as "not running yet."
///
/// The runtime flavor (net472 vs net10.0 — which enlist-runner build an agent needs to host this) is
/// no longer declared here either: the control plane detects it automatically from the uploaded
/// build's own shape (presence or absence of a .deps.json entry) and returns it below for confirmation.
///
/// A control plane that requires authentication wants a management API key with the Operator role
/// (Authentication-Design.md 6.2): --api-key, or ENLIST_API_KEY in the environment, which is what a
/// pipeline uses because a command line is visible in process listings. Against a control plane whose
/// authentication is Off (loopback only) neither is needed.
/// </summary>
internal static class Program
{
    private const string ApiKeyVariable = "ENLIST_API_KEY";

    private static async Task<int> Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Usage: enlist-deploy --control-plane <url> --app <name> --source <dir> [--api-key <key>]   (or {ApiKeyVariable} in the environment)");
            return 2;
        }

        using var http = new HttpClient { BaseAddress = options.ControlPlaneUrl };
        if (options.ApiKey is { } apiKey)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        Console.WriteLine($"Packaging {options.SourceDir}...");
        try
        {
            await UploadPackageAsync(http, options.SourceDir, options.AppName, options.ApiKey is not null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        Console.WriteLine("Done. Declare where this runs from the Applications tab in the portal.");
        return 0;
    }

    private static async Task UploadPackageAsync(HttpClient http, string sourceDir, string appName, bool presentedKey)
    {
        var tempZip = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            ZipFile.CreateFromDirectory(sourceDir, tempZip, CompressionLevel.Optimal, includeBaseDirectory: false);

            await using var stream = File.OpenRead(tempZip);
            var response = await http.PostAsync($"/api/packages?application={Uri.EscapeDataString(appName)}", new StreamContent(stream));

            // The two refusals that are about the caller rather than the package say what to do; the
            // rest quote the server's own words - a refused name, an unknown digest or a body over the
            // limit each say exactly what to change.
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new InvalidOperationException(presentedKey
                    ? "The control plane refused the API key (401): it is unknown, revoked or expired. An Operator creates another with 'Enlist.ControlPlane create-api-key --name <name> --role Operator', or from the portal's Access page."
                    : $"The control plane requires an API key (401). Pass --api-key <key> or set {ApiKeyVariable}; an Operator creates one with 'Enlist.ControlPlane create-api-key --name <name> --role Operator', or from the portal's Access page.");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new InvalidOperationException("The API key presented has the Viewer role (403); uploading a package needs Operator - it is code every qualifying agent will run.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var detail = (await response.Content.ReadAsStringAsync()).Trim();
                throw new InvalidOperationException(
                    $"The control plane refused the upload: {(int)response.StatusCode} {response.ReasonPhrase}" +
                    (detail.Length == 0 ? "" : $" - {detail}"));
            }

            var result = await response.Content.ReadFromJsonAsync<UploadPackageResponse>();
            Console.WriteLine(result!.AlreadyExisted
                ? $"  ({result.SizeBytes:N0} bytes - already on the control plane, nothing to upload)"
                : $"  ({result.SizeBytes:N0} bytes uploaded)");
            Console.WriteLine($"  digest: {result.Digest}");
            Console.WriteLine($"  runtime flavor detected: {result.RuntimeFlavor}");
        }
        finally
        {
            File.Delete(tempZip);
        }
    }

    private sealed record Options(Uri ControlPlaneUrl, string AppName, string SourceDir, string? ApiKey)
    {
        public static Options Parse(string[] args)
        {
            string? controlPlane = null;
            string? appName = null;
            string? sourceDir = null;
            string? apiKey = null;

            // Unknown flags are refused, and a flag in the last position is not skipped. Both used to
            // pass silently, and the consequence was specific and bad: a mistyped `--api_key` left the
            // key unset, so the upload went out unauthenticated and the 401 that came back blamed a
            // missing key rather than the typo that caused it.
            for (var i = 0; i < args.Length; i++)
            {
                var flag = args[i];
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"'{flag}' needs a value.");
                }

                switch (flag)
                {
                    case "--control-plane": controlPlane = args[++i]; break;
                    case "--app": appName = args[++i]; break;
                    case "--source": sourceDir = args[++i]; break;
                    case "--api-key": apiKey = args[++i]; break;
                    default: throw new ArgumentException($"Unknown option '{flag}'. Valid options: --control-plane, --app, --source, --api-key.");
                }
            }

            if (controlPlane is null || appName is null || sourceDir is null)
            {
                throw new ArgumentException("--control-plane, --app and --source are all required.");
            }

            if (!Directory.Exists(sourceDir))
            {
                throw new ArgumentException($"--source directory not found: {sourceDir}");
            }

            if (!ApplicationNames.IsValid(appName))
            {
                throw new ArgumentException($"--app '{appName}' is not a valid application name: {ApplicationNames.Requirement}.");
            }

            // The command line wins over the environment when both are given; an empty variable is no key.
            apiKey ??= Environment.GetEnvironmentVariable(ApiKeyVariable);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                apiKey = null;
            }

            return new Options(new Uri(controlPlane.TrimEnd('/') + "/"), appName, sourceDir, apiKey);
        }
    }
}
