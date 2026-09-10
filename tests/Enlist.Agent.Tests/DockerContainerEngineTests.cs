using System.Diagnostics;

using Enlist.Agent.Supervision;

namespace Enlist.Agent.Tests;

/// <summary>
/// The engine's own timeout, against a stand-in `docker` that never answers. A daemon that accepts
/// the connection and then hangs used to hang the agent with it — `StartAsync`, and behind it the
/// whole reconcile loop, waited on `docker run` with no limit. No real engine is involved here, so
/// this runs on a machine with no Docker at all.
/// </summary>
public sealed class DockerContainerEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "enlist-engine-tests-" + Guid.NewGuid().ToString("N"));

    public DockerContainerEngineTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    [SkippableFact]
    public async Task A_docker_command_that_never_returns_is_killed_and_reported_after_the_timeout()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "the stand-in engine is a .cmd script");

        // Ignores its arguments and sleeps a minute — from the engine's side, a daemon that never answers.
        var stub = Path.Combine(_dir, "hung-docker.cmd");
        await File.WriteAllTextAsync(stub, "@echo off\r\nping -n 60 127.0.0.1 > nul\r\n");

        var engine = new DockerContainerEngine(stub, commandTimeout: TimeSpan.FromSeconds(1));

        var sw = Stopwatch.StartNew();
        var probe = await engine.ProbeAsync();
        sw.Stop();

        Assert.False(probe.Available);
        Assert.Contains("did not finish within", probe.Error);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"the probe took {sw.Elapsed.TotalSeconds:0.0}s - the timeout did not cut the hung command short");

        // Every other command surfaces the same condition as an exception the caller can act on.
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => engine.ListByLabelsAsync(new Dictionary<string, string> { ["enlist.agent"] = "x" }));
        Assert.Contains("ps", ex.Message);
    }
}
