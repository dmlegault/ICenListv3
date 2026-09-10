using System.IO.Compression;
using System.Net.Http.Json;

using Enlist.ControlPlane.Contracts;

namespace Enlist.Deploy;

/// <summary>
/// enlist-deploy --control-plane &lt;url&gt; --app &lt;name&gt; --source &lt;dir&gt;
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
/// </summary>
internal static class Program
{
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
            Console.Error.WriteLine("Usage: enlist-deploy --control-plane <url> --app <name> --source <dir>");
            return 2;
        }

        using var http = new HttpClient { BaseAddress = options.ControlPlaneUrl };

        Console.WriteLine($"Packaging {options.SourceDir}...");
        try
        {
            await UploadPackageAsync(http, options.SourceDir, options.AppName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        Console.WriteLine("Done. Declare where this runs from the Applications tab in the portal.");
        return 0;
    }

    private static async Task UploadPackageAsync(HttpClient http, string sourceDir, string appName)
    {
        var tempZip = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            ZipFile.CreateFromDirectory(sourceDir, tempZip, CompressionLevel.Optimal, includeBaseDirectory: false);

            await using var stream = File.OpenRead(tempZip);
            var response = await http.PostAsync($"/api/packages?application={Uri.EscapeDataString(appName)}", new StreamContent(stream));
            // The server's own words, not a bare status code: a refused name, an unknown digest or a
            // body over the limit each say exactly what to change.
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

    private sealed record Options(Uri ControlPlaneUrl, string AppName, string SourceDir)
    {
        public static Options Parse(string[] args)
        {
            string? controlPlane = null;
            string? appName = null;
            string? sourceDir = null;

            for (var i = 0; i < args.Length - 1; i++)
            {
                switch (args[i])
                {
                    case "--control-plane": controlPlane = args[++i]; break;
                    case "--app": appName = args[++i]; break;
                    case "--source": sourceDir = args[++i]; break;
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

            return new Options(new Uri(controlPlane.TrimEnd('/') + "/"), appName, sourceDir);
        }
    }
}
