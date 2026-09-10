namespace Enlist.Runner.Tests;

internal static class RepoPaths
{
    public static string SampleServiceOutputDir() => Path.Combine(Root(), "deploy", "SampleService");

    /// <summary>The richer sample — four services, five jobs, and the only one with a long-running job that honors cancellation.</summary>
    public static string OrderProcessorOutputDir() => Path.Combine(Root(), "deploy", "OrderProcessor");

    /// <summary>The net472 sample, in the same deployed shape — one service, one job, one cancellable batch.</summary>
    public static string LegacySampleOutputDir() => Path.Combine(Root(), "deploy", "LegacySample");

    /// <summary>The net472 runner's own build output. Located by path rather than referenced — see StubAgent.StartLegacyAsync.</summary>
    public static string LegacyRunnerExe() => Path.Combine(Root(), "src", "Enlist.Runner.Legacy", "bin", "Debug", "net472", "enlist-runner.exe");

    /// <summary>The repo root, for tests that need to run a process with a KNOWN working directory — see CheckModeTests relative-path regression.</summary>
    public static string RepositoryRoot() => Root();

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "enList_v3.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException(
            $"Could not find enList_v3.slnx walking up from {AppContext.BaseDirectory}.");
    }
}
