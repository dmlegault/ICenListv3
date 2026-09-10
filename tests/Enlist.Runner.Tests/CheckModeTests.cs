using System.Diagnostics;

namespace Enlist.Runner.Tests;

/// <summary>
/// `enlist-runner --check` — discovery only, no transport, nothing started.
///
/// Its exit code is a documented contract (Application-Developer-Guide.md §3): 0 when at least one
/// service or job was discovered, 1 when nothing was. That is the half worth pinning, because the
/// failure it catches has no other symptom — an application that discovers nothing still reports
/// Running, shows green in the portal, and does absolutely nothing. The guide tells people to gate a
/// build on this, so the codes must not drift.
/// </summary>
public sealed class CheckModeTests : IAsyncLifetime
{
    private readonly string _emptyDir = Path.Combine(Path.GetTempPath(), "enlist-check-empty-" + Guid.NewGuid().ToString("N"));

    // IAsyncLifetime, NOT IAsyncDisposable — xUnit 2.9.3 does not invoke IAsyncDisposable on a test
    // class, so declaring that interface silently means no cleanup at all. Measured, not assumed:
    // this test leaked exactly one directory per run until it was changed.
    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_emptyDir, recursive: true);
        }
        catch
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Check_discovers_the_real_sample_and_exits_zero()
    {
        var (exitCode, output) = await RunCheckAsync(RepoPaths.SampleServiceOutputDir());

        Assert.Equal(0, exitCode);

        // The names, not just a count — a report that says "1 service" without naming it cannot tell
        // you whether it found the one you just wrote.
        Assert.Contains("Sample Service", output);
        Assert.Contains("Discovered", output);
    }

    [Fact]
    public async Task Check_exits_nonzero_and_says_what_to_look_at_when_nothing_is_discovered()
    {
        Directory.CreateDirectory(_emptyDir);

        var (exitCode, output) = await RunCheckAsync(_emptyDir);

        Assert.Equal(1, exitCode);

        // The actionable half. "Running but empty" is the worst failure mode enList has, so this path
        // has to name the usual causes rather than only reporting zero.
        Assert.Contains("Nothing was discovered", output);
        Assert.Contains("[EnlistService]", output);
    }

    /// <summary>
    /// Regression: a relative --app path made the load context throw ArgumentException for EVERY
    /// plugin assembly ("is not an absolute path"), so discovery silently returned nothing at all.
    ///
    /// The agent always passes an absolute path, which is why this survived until --check was pointed
    /// at a relative directory from a shell — and the symptom was maximally misleading, since it looks
    /// exactly like an application whose attributes are wrong.
    /// </summary>
    [Fact]
    public async Task Check_resolves_a_relative_app_path_against_the_working_directory()
    {
        var (exitCode, output) = await RunCheckAsync(
            Path.Combine("deploy", "SampleService"),
            workingDirectory: RepoPaths.RepositoryRoot());

        Assert.Equal(0, exitCode);
        Assert.Contains("Sample Service", output);
        Assert.DoesNotContain("is not an absolute path", output);
    }

    [Fact]
    public async Task Check_and_dev_cannot_be_combined()
    {
        var (exitCode, output) = await RunCheckAsync(RepoPaths.SampleServiceOutputDir(), extraArgs: ["--dev"]);

        // Each mode is a complete answer to "who drives this runner?" — silently preferring one would
        // start services under a flag that asked for a report.
        Assert.Equal(2, exitCode);
        Assert.Contains("exactly one", output);
    }

    private static async Task<(int ExitCode, string Output)> RunCheckAsync(
        string appPath, string? workingDirectory = null, string[]? extraArgs = null)
    {
        var runnerDll = Path.Combine(AppContext.BaseDirectory, "enlist-runner.dll");
        if (!File.Exists(runnerDll))
        {
            throw new FileNotFoundException(
                $"enlist-runner.dll not found next to the test assembly at {runnerDll} - expected the ProjectReference to Enlist.Runner to copy it there.",
                runnerDll);
        }

        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? AppContext.BaseDirectory,
        };

        psi.ArgumentList.Add(runnerDll);
        psi.ArgumentList.Add("--check");
        psi.ArgumentList.Add("--app");
        psi.ArgumentList.Add(appPath);

        foreach (var argument in extraArgs ?? [])
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start enlist-runner.");

        // Both streams, because the usage/validation failures write to stderr while the report writes
        // to stdout, and these tests assert on each.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(cts.Token);

        return (process.ExitCode, await stdout + await stderr);
    }
}
