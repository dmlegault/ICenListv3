using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;

using Enlist.ControlPlane.Contracts;
using Enlist.TestSupport;

namespace Enlist.Deploy.Tests;

/// <summary>
/// enlist-deploy publishes a package — that's its entire job now. Placement (which agents run it) is
/// an exclusively-portal decision made afterward, so these tests only ever assert upload behavior:
/// idempotent re-upload, and that the package is queryable afterward with no assignment/policy
/// anywhere pointing at it yet — a freshly-deployed, unplaced package is the expected end state of
/// this CLI, not an intermediate one. Runs the real enlist-deploy.exe as its own process against a
/// real control plane, same pattern as every other CLI test in this suite. The last three run against
/// a control plane that requires authentication, the way a real one does.
/// </summary>
public sealed class DeployCliTests : IAsyncLifetime
{
    private static readonly IReadOnlyDictionary<string, string> Required = new Dictionary<string, string> { ["Authentication__Mode"] = "Required" };

    private ControlPlaneTestServer? _server;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _server = await ControlPlaneTestServer.StartAsync(TimeSpan.FromSeconds(30));
        _client = new HttpClient { BaseAddress = _server.BaseUri };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();

        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_mistyped_option_is_refused_rather_than_ignored()
    {
        // The consequence was specific: `--api_key` was silently dropped, the upload went out with no
        // credential at all, and the 401 that came back blamed a missing key rather than the
        // underscore that caused it. The person is then looking at their key, which is fine, instead
        // of at the flag, which is not.
        var mistyped = await RunDeployAsync("--control-plane", "http://localhost:1", "--app", "X", "--source", ".", "--api_key", "enlk_whatever");

        Assert.NotEqual(0, mistyped.ExitCode);
        Assert.Contains("--api_key", mistyped.Output);
        Assert.Contains("--api-key", mistyped.Output);
    }

    [Fact]
    public async Task An_option_with_no_value_is_refused_rather_than_ignored()
    {
        // The loop stopped one short of the end, so a flag in the LAST position was never read: this
        // used to fail as "--source is required" for a command line that plainly has one.
        var truncated = await RunDeployAsync("--control-plane", "http://localhost:1", "--app", "X", "--source");

        Assert.NotEqual(0, truncated.ExitCode);
        Assert.Contains("--source", truncated.Output);
        Assert.Contains("needs a value", truncated.Output);
    }

    private static Task<(int ExitCode, string Output)> RunDeployAsync(params string[] args) => RunDeployAsync(null, args);

    private static async Task<(int ExitCode, string Output)> RunDeployAsync(IReadOnlyDictionary<string, string>? environment, params string[] args)
    {
        var dll = RepoPaths.DeployDll();
        Assert.True(File.Exists(dll), $"enlist-deploy.dll not found at {dll} - build src/Enlist.Deploy first.");

        var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(dll);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        // Never inherited from the test host: a key in this shell must not make the "no key" test pass.
        psi.Environment.Remove("ENLIST_API_KEY");
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                psi.Environment[key] = value;
            }
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start enlist-deploy.");
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return (process.ExitCode, output.ToString());
    }

    [Fact]
    public async Task Deploying_uploads_a_package_visible_to_the_control_plane_with_no_placement_anywhere()
    {
        var appName = RepoPaths.UniqueAppName();

        var (exitCode, output) = await RunDeployAsync(
            "--control-plane", _server!.BaseUri.ToString(),
            "--app", appName,
            "--source", RepoPaths.SampleServiceDir());

        Assert.Equal(0, exitCode);
        Assert.Contains("uploaded", output);
        Assert.Contains("digest:", output);
        Assert.Contains("runtime flavor detected:", output);

        var packages = await _client.GetFromJsonAsync<List<PackageInfo>>("/api/packages");
        var package = Assert.Single(packages!, p => p.ApplicationName == appName);
        Assert.Equal(1, package.VersionNumber);
        Assert.Equal(RuntimeFlavors.Default, package.RuntimeFlavor); // SampleServiceDir is a net10.0 build

        // No policy rule anywhere references this app — placement is a portal-only decision now.
        var policies = await _client.GetFromJsonAsync<List<ApplicationPolicyDto>>("/api/application-policies");
        Assert.DoesNotContain(policies!, a => a.ApplicationName == appName);
    }

    [Fact]
    public async Task Deploying_the_same_build_twice_is_a_no_op_upload_not_a_second_package()
    {
        var appName = RepoPaths.UniqueAppName();
        string[] args = ["--control-plane", _server!.BaseUri.ToString(), "--app", appName, "--source", RepoPaths.SampleServiceDir()];

        var first = await RunDeployAsync(args);
        Assert.Equal(0, first.ExitCode);
        Assert.Contains("uploaded", first.Output);

        var second = await RunDeployAsync(args);
        Assert.Equal(0, second.ExitCode);
        Assert.Contains("already on the control plane", second.Output);

        var packages = await _client.GetFromJsonAsync<List<PackageInfo>>("/api/packages");
        Assert.Single(packages!, p => p.ApplicationName == appName); // still exactly one row, not two
    }

    [Fact]
    public async Task Deploying_a_net472_build_is_detected_as_the_legacy_runtime_flavor()
    {
        var appName = RepoPaths.UniqueAppName();

        var (exitCode, output) = await RunDeployAsync(
            "--control-plane", _server!.BaseUri.ToString(),
            "--app", appName,
            "--source", RepoPaths.LegacySampleDir());

        Assert.Equal(0, exitCode);
        Assert.Contains($"runtime flavor detected: {RuntimeFlavors.NetFramework472}", output);

        var packages = await _client.GetFromJsonAsync<List<PackageInfo>>("/api/packages");
        var package = Assert.Single(packages!, p => p.ApplicationName == appName);
        Assert.Equal(RuntimeFlavors.NetFramework472, package.RuntimeFlavor);
    }

    [Fact]
    public async Task Against_a_control_plane_that_requires_authentication_an_operator_key_uploads_and_no_key_is_told_what_to_do()
    {
        await using var required = await ControlPlaneTestServer.StartAsync(TimeSpan.FromSeconds(30), Required);
        var operatorKey = await required.CreateApiKeyAsync("ci-main", "Operator");
        string[] upload = ["--control-plane", required.BaseUri.ToString(), "--app", RepoPaths.UniqueAppName(), "--source", RepoPaths.SampleServiceDir()];

        // No key: refused with the remedy, not a bare status code.
        var refused = await RunDeployAsync(upload);
        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("--api-key", refused.Output);
        Assert.Contains("ENLIST_API_KEY", refused.Output);

        // On the command line.
        var withFlag = await RunDeployAsync([.. upload, "--api-key", operatorKey]);
        Assert.Equal(0, withFlag.ExitCode);
        Assert.Contains("uploaded", withFlag.Output);

        // In the environment, the way a pipeline passes it.
        var withEnvironment = await RunDeployAsync(new Dictionary<string, string> { ["ENLIST_API_KEY"] = operatorKey }, upload);
        Assert.Equal(0, withEnvironment.ExitCode);
        Assert.Contains("already on the control plane", withEnvironment.Output);
    }

    [Fact]
    public async Task A_viewer_key_cannot_upload_and_is_told_why()
    {
        await using var required = await ControlPlaneTestServer.StartAsync(TimeSpan.FromSeconds(30), Required);
        var viewerKey = await required.CreateApiKeyAsync("dashboard", "Viewer");

        var (exitCode, output) = await RunDeployAsync(
            "--control-plane", required.BaseUri.ToString(),
            "--app", RepoPaths.UniqueAppName(),
            "--source", RepoPaths.SampleServiceDir(),
            "--api-key", viewerKey);

        Assert.Equal(1, exitCode);
        Assert.Contains("Viewer", output);
        Assert.Contains("Operator", output);
    }
}
