using Enlist.Runner.Discovery;
using Enlist.Runner.Protocol;

namespace Enlist.Runner.Dev;

/// <summary>
/// `enlist-runner --check --app &lt;dir&gt;` — runs discovery, prints exactly what enList would see, and
/// exits without loading a transport or starting anything.
///
/// This answers the single most common plugin-authoring question ("why didn't my job show up?") in
/// about a second, instead of after a build/zip/upload/policy/reconcile round trip. The usual causes
/// — a non-public class, a missing [EnlistExecute], a misspelled attribute, an assembly that failed
/// to load — are all visible here, and the load warnings in particular are otherwise only ever seen
/// buried in an agent log.
///
/// Nothing plugin-authored executes on this path: discovery reflects over types, it does not
/// construct them. So it is safe to point at an untrusted build output.
/// </summary>
internal static class CheckReport
{
    /// <summary>
    /// Exit code 0 when at least one service or job was discovered, 1 when nothing was — which is the
    /// genuinely actionable outcome and makes this usable as a build-pipeline gate. Warnings alone do
    /// NOT fail: a publish output full of native DLLs legitimately produces them, and failing on that
    /// would train people to ignore the flag.
    /// </summary>
    public static int Write(string appPath, DiscoveryResult discovery, IsolationInfo isolation)
    {
        var output = Console.Out;

        output.WriteLine();
        output.WriteLine($"Application directory: {Path.GetFullPath(appPath)}");
        output.WriteLine(string.IsNullOrWhiteSpace(discovery.ApplicationDescription)
            ? "Description:           (none - add [assembly: EnlistApplication(Description = \"...\")])"
            : $"Description:           {discovery.ApplicationDescription}");

        output.WriteLine();
        output.WriteLine($"Services ({discovery.Services.Count}):");
        if (discovery.Services.Count == 0)
        {
            output.WriteLine("  (none)");
        }

        foreach (var service in discovery.Services)
        {
            output.WriteLine($"  - {service.Name}");
            output.WriteLine($"      type:        {service.TypeName}");
            if (!string.IsNullOrWhiteSpace(service.Description))
            {
                output.WriteLine($"      description: {service.Description}");
            }
        }

        output.WriteLine();
        output.WriteLine($"Jobs ({discovery.Jobs.Count}):");
        if (discovery.Jobs.Count == 0)
        {
            output.WriteLine("  (none)");
        }

        foreach (var job in discovery.Jobs)
        {
            output.WriteLine($"  - {job.Name}");
            output.WriteLine($"      type:        {job.TypeName}");

            // Named explicitly rather than omitted when absent: "no declared cron" is a normal,
            // deliberate state (the policy supplies one instead), and a silent gap reads as a bug.
            output.WriteLine($"      cron:        {job.DeclaredCron ?? "(none declared - a policy cron override must supply one)"}");
            if (!string.IsNullOrWhiteSpace(job.Description))
            {
                output.WriteLine($"      description: {job.Description}");
            }
        }

        output.WriteLine();
        output.WriteLine("Assembly isolation:");
        output.WriteLine($"  .deps.json files found:      {isolation.DepsFilesFound}");
        output.WriteLine($"  private resolvers:           {isolation.ResolverCount}");
        output.WriteLine($"  privately resolved requests: {isolation.PrivateResolutionCount}");
        if (isolation.OrphanedDeps.Count > 0)
        {
            output.WriteLine($"  orphaned deps:               {string.Join(", ", isolation.OrphanedDeps)}");
        }

        output.WriteLine();
        if (discovery.Warnings.Count == 0)
        {
            output.WriteLine("Warnings: none.");
        }
        else
        {
            output.WriteLine($"Warnings ({discovery.Warnings.Count}):");
            foreach (var warning in discovery.Warnings)
            {
                output.WriteLine($"  - {warning}");
            }
        }

        output.WriteLine();

        var total = discovery.Services.Count + discovery.Jobs.Count;
        if (total == 0)
        {
            output.WriteLine("Nothing was discovered. enList would report this application as running but empty.");
            output.WriteLine("Check that your types are public, non-abstract, carry [EnlistService] or [EnlistJob],");
            output.WriteLine("and that a [EnlistStart] / [EnlistExecute] method is present and public.");
            return 1;
        }

        output.WriteLine($"Discovered {discovery.Services.Count} service(s) and {discovery.Jobs.Count} job(s).");
        return 0;
    }
}
