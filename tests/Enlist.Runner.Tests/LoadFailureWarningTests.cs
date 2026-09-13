using Enlist.Runner.Protocol;

namespace Enlist.Runner.Tests;

/// <summary>
/// An assembly that cannot be loaded is skipped — one bad file must never fail a whole application —
/// but the skip has to be VISIBLE.
///
/// Without it the failure surfaces only as "fewer services and jobs than expected",
/// which is not something anyone can search for or act on: the application reports Running with
/// nothing in it and no error anywhere. That silent shape is exactly why a net472 package assigned to
/// a Linux container appeared to work while doing nothing.
/// </summary>
public sealed class LoadFailureWarningTests : IAsyncLifetime
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(15);

    private readonly string _appDir = Path.Combine(Path.GetTempPath(), "enlist-loadfail-" + Guid.NewGuid().ToString("N"));

    // IAsyncLifetime, NOT IAsyncDisposable: xUnit 2.9.3 does not invoke IAsyncDisposable on a test
    // class, so the previous declaration meant this never ran at all — 50 stale directories had
    // accumulated under %TEMP% before anyone counted them.
    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_appDir, recursive: true);
        }
        catch
        {
        }

        return Task.CompletedTask;
    }

    /// <summary>A copy of the real sample application plus one deliberately broken file, so the good half must still be discovered while the bad half is reported.</summary>
    private void StageAppWithABrokenDll(string brokenFileName, byte[] brokenContent)
    {
        Directory.CreateDirectory(_appDir);

        foreach (var file in Directory.EnumerateFiles(RepoPaths.SampleServiceOutputDir()))
        {
            File.Copy(file, Path.Combine(_appDir, Path.GetFileName(file)), overwrite: true);
        }

        File.WriteAllBytes(Path.Combine(_appDir, brokenFileName), brokenContent);
    }

    [Fact]
    public async Task A_file_that_is_not_a_managed_assembly_is_reported_as_skipped()
    {
        // Not a PE image at all — the shape a native dependency has from the loader's point of view.
        StageAppWithABrokenDll("native-ish.dll", "this is not an assembly"u8.ToArray());

        await using var agent = await StubAgent.StartAsync(_appDir, Step);
        var ready = await Receive<ReadyMessage>(agent);

        // The real plugin is still found: skipping is per-file, never per-application.
        Assert.Contains(ready.Services, s => s.Name == "Sample Service");

        // And the skip is stated rather than inferred, naming the file. Grouped into one line, and
        // capped, so a publish output full of native libraries stays readable.
        Assert.Contains(ready.Warnings, w => w.Contains("native-ish.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_corrupt_managed_assembly_is_named_in_the_warnings()
    {
        // A real assembly's first bytes, then garbage: enough to get past "is this a PE file at all"
        // and fail as something that should have loaded.
        var realDll = await File.ReadAllBytesAsync(
            Path.Combine(RepoPaths.SampleServiceOutputDir(), "Enlist.Sample.Service.dll"));

        var truncated = realDll[..(realDll.Length / 3)];
        StageAppWithABrokenDll("Broken.Plugin.dll", truncated);

        await using var agent = await StubAgent.StartAsync(_appDir, Step);
        var ready = await Receive<ReadyMessage>(agent);

        Assert.Contains(ready.Services, s => s.Name == "Sample Service");

        // NAMED, which is the point. A truncated managed assembly raises the same BadImageFormatException
        // as a native DLL — the runtime cannot tell them apart — so these are grouped for brevity but
        // never anonymous. An unnamed count would hide precisely the file worth investigating.
        Assert.Contains(ready.Warnings, w => w.Contains("Broken.Plugin.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_clean_application_produces_no_load_warnings_at_all()
    {
        await using var agent = await StubAgent.StartAsync(RepoPaths.SampleServiceOutputDir(), Step);
        var ready = await Receive<ReadyMessage>(agent);

        // The guard against the obvious failure mode of this feature: warnings nobody reads because
        // every healthy application emits them too.
        Assert.Empty(ready.Warnings);
    }

    private static Task<T> Receive<T>(StubAgent agent) where T : RunnerMessage => agent.NextAsync<T>(Step);
}
