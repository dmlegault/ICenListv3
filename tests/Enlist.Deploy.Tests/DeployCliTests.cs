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
/// real control plane, same pattern as every other CLI test in this suite.
/// </summary>
public sealed class DeployCliTests : IAsyncLifetime
{
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

    private async Task<(int ExitCode, string Output)> RunDeployAsync(params string[] args)
    {
        var dll = RepoPaths.DeployDll();
        Assert.True(File.Exists(dll), $"enlist-deploy.dll not found at {dll} - build src/Enlist.Deploy first.");

        var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(dll);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
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
}
