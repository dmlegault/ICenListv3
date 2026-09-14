using System.Runtime.Versioning;

using Enlist.Installer.Detection;

namespace Enlist.Installer.Detection.Tests;

/// <summary>
/// Probing wslc and docker on this machine, whatever it happens to have.
///
/// These assert on the SHAPE of the answer rather than on which engines are present, because the
/// answer has to be usable either way: the Agent page enables a radio when an engine is there and
/// says why beside it when it is not, and "why not" is the half nobody writes. An agent with no
/// engine is a perfectly good process-only agent, so absence is never a failure here.
/// </summary>
public sealed class ContainerEngineDetectionTests
{
    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public async Task Probing_wslc_answers_within_the_page_timeout_either_way()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Both engines are probed through Windows executables.");

        var started = DateTime.UtcNow;
        var result = await ContainerEngineDetection.ProbeWslcAsync();
        var took = DateTime.UtcNow - started;

        Assert.Equal("wslc", result.Engine);
        Assert.False(string.IsNullOrWhiteSpace(result.Detail), "an unavailable engine still has to say why");

        // The page shows a spinner while this runs. Exceeding its own timeout by a wide margin would
        // mean the timeout is not being applied, which is the bug this catches.
        Assert.True(took < ContainerEngineDetection.ProbeTimeout + TimeSpan.FromSeconds(5),
            $"the probe took {took.TotalSeconds:0.0}s against a {ContainerEngineDetection.ProbeTimeout.TotalSeconds:0}s timeout");
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public async Task Probing_docker_reports_the_daemon_rather_than_the_client()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Both engines are probed through Windows executables.");

        var result = await ContainerEngineDetection.ProbeDockerAsync();

        Assert.Equal("docker", result.Engine);
        Assert.False(string.IsNullOrWhiteSpace(result.Detail));

        // The distinction that matters: the CLI installs separately from the engine, so "docker is on
        // PATH" is not "docker works". When this machine has the CLI and a stopped daemon, the probe
        // must say unavailable - which is what asking for .Server.Version achieves.
        if (!result.Available)
        {
            Assert.True(
                result.Detail.Contains("exited") || result.Detail.Contains("not answer") ||
                result.Detail.Contains("cannot find") || result.Detail.Contains("system cannot"),
                $"an unavailable docker should say why, got: {result.Detail}");
        }
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public async Task Both_engines_can_be_probed_at_once_so_the_page_waits_once_not_twice()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Both engines are probed through Windows executables.");

        var started = DateTime.UtcNow;
        var both = await Task.WhenAll(ContainerEngineDetection.ProbeWslcAsync(), ContainerEngineDetection.ProbeDockerAsync());
        var took = DateTime.UtcNow - started;

        Assert.Equal(2, both.Length);
        Assert.True(took < ContainerEngineDetection.ProbeTimeout + TimeSpan.FromSeconds(5),
            $"probing both took {took.TotalSeconds:0.0}s, which suggests they ran one after the other");
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void Wslc_is_looked_for_where_its_installer_puts_it_not_only_on_PATH()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "This is a Windows path.");

        // WSL's installer routinely leaves wslc out of PATH. Falling back to the bare name when the
        // install directory has nothing is deliberate, so a machine where it IS on PATH still works.
        var path = ContainerEngineDetection.WslcPath();
        Assert.True(path == "wslc" || path.EndsWith("wslc.exe", StringComparison.OrdinalIgnoreCase), path);
    }

    /// <summary>
    /// The Agent page's default engine: Docker when it answers, otherwise none - never wslc. wslc keeps a
    /// separate image store per account, so the image an operator loads is not the LocalSystem agent's;
    /// Docker's one store is shared.
    /// </summary>
    [Theory]
    [InlineData(true, true, "docker")]    // both: this machine, where "first found" used to pick wslc
    [InlineData(false, true, "docker")]
    [InlineData(true, false, null)]       // wslc alone is still not proposed: its image store is per account
    [InlineData(false, false, null)]
    public void The_default_engine_is_docker_when_it_answers_and_never_wslc(bool wslcUp, bool dockerUp, string? expected)
    {
        // wslc first, as the wizard probes them, so an order-based default would pick it.
        var engines = new[]
        {
            new EngineResult("wslc", wslcUp, wslcUp ? "wslc 2.9.11.0" : "not installed"),
            new EngineResult("docker", dockerUp, dockerUp ? "29.7.2" : "not running"),
        };

        Assert.Equal(expected, ContainerEngineDetection.DefaultEngine(engines));
    }
}
