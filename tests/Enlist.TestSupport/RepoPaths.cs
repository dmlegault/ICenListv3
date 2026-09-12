namespace Enlist.TestSupport;

public static class RepoPaths
{
    public static string SampleServiceDir() => Path.Combine(Root(), "deploy", "SampleService");

    /// <summary>The richer sample — its Ledger Rebuild job runs long enough to be caught mid-flight, which the one-shot Sample Job cannot be.</summary>
    public static string OrderProcessorDir() => Path.Combine(Root(), "deploy", "OrderProcessor");

    /// <summary>A real net472 build output — no .deps.json, unlike every other sample here — used to exercise runtime-flavor auto-detection against something genuinely legacy-shaped, not just a fixture pretending to be.</summary>
    public static string LegacySampleDir() => Path.Combine(Root(), "deploy", "LegacySample");

    public static string RunnerBinDirectory() => Path.Combine(Root(), "src", "Enlist.Runner", "bin", "Debug", "net10.0");

    public static string ControlPlaneDll() => Path.Combine(Root(), "src", "Enlist.ControlPlane", "bin", "Debug", "net10.0", "Enlist.ControlPlane.dll");

    public static string DeployDll() => Path.Combine(Root(), "src", "Enlist.Deploy", "bin", "Debug", "net10.0", "enlist-deploy.dll");

    public static string PortalDll() => Path.Combine(Root(), "src", "Enlist.Portal", "bin", "Debug", "net10.0", "Enlist.Portal.dll");

    /// <summary>
    /// A fresh, process-name-safe application name. Several test classes stage and spawn a real
    /// process under this name and then query/kill by that OS-level process name (Process
    /// .GetProcessesByName), which is global to the machine — reusing the literal "SampleService"
    /// across classes running in parallel caused exactly the cross-test interference this exists to
    /// avoid (one test's cleanup killing another test's still-running process). The plugin's own
    /// [EnlistService]/[EnlistJob] names ("Sample Service", "Sample Job") are unaffected — those come
    /// from the plugin's attributes, not the assignment's application name.
    /// </summary>
    public static string UniqueAppName() => "SampleSvc" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>The repository root, for a test whose subject is the source tree itself rather than anything built from it — see AsciiTextRuleTests.</summary>
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
