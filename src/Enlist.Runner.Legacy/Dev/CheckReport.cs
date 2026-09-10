using Enlist.Runner.Legacy.Discovery;
using Enlist.Runner.Legacy.Protocol;

namespace Enlist.Runner.Legacy.Dev;

/// <summary>
/// `enlist-runner --check --app &lt;dir&gt;` for net472 applications — the legacy counterpart to
/// Enlist.Runner/Dev/CheckReport.cs, kept deliberately identical in output so the two flavours are
/// diagnosed the same way.
///
/// Answers "why didn't my job show up?" in about a second rather than after a
/// build/zip/upload/policy/reconcile round trip. The usual causes — a non-public class, a missing
/// [EnlistExecute], a misspelled attribute, an assembly that failed to load — are all visible here,
/// and the load warnings in particular are otherwise only ever seen buried in an agent log.
///
/// Nothing plugin-authored executes on this path: discovery reflects over types, it does not
/// construct them.
/// </summary>
internal static class CheckReport
{
    /// <summary>
    /// Exit code 0 when at least one service or job was discovered, 1 when nothing was — the
    /// genuinely actionable outcome, and what makes this usable as a build-pipeline gate. Warnings
    /// alone do NOT fail: a publish output full of native DLLs legitimately produces them, and
    /// failing on that would train people to ignore the flag.
    /// </summary>
    public static int Write(string appPath, DiscoveryResult discovery, IsolationInfo isolation)
    {
        var output = Console.Out;

        output.WriteLine();
        output.WriteLine("Application directory: " + Path.GetFullPath(appPath));
        output.WriteLine(string.IsNullOrWhiteSpace(discovery.ApplicationDescription)
            ? "Description:           (none - add [assembly: EnlistApplication(Description = \"...\")])"
            : "Description:           " + discovery.ApplicationDescription);

        output.WriteLine();
        output.WriteLine($"Services ({discovery.Services.Count}):");
        if (discovery.Services.Count == 0)
        {
            output.WriteLine("  (none)");
        }

        foreach (var service in discovery.Services)
        {
            output.WriteLine("  - " + service.Name);
            output.WriteLine("      type:        " + service.TypeName);
            if (!string.IsNullOrWhiteSpace(service.Description))
            {
                output.WriteLine("      description: " + service.Description);
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
            output.WriteLine("  - " + job.Name);
            output.WriteLine("      type:        " + job.TypeName);

            // Named explicitly rather than omitted when absent: "no declared cron" is a normal,
            // deliberate state (the policy supplies one instead), and a silent gap reads as a bug.
            output.WriteLine("      cron:        " + (job.DeclaredCron ?? "(none declared - a policy cron override must supply one)"));
            if (!string.IsNullOrWhiteSpace(job.Description))
            {
                output.WriteLine("      description: " + job.Description);
            }
        }

        output.WriteLine();
        output.WriteLine("Assembly isolation:");

        // Honest zeros on this runner — see LegacyPluginLoadContext for why net472 has no
        // resolver-based isolation to report. Printed anyway so the two flavours' --check output can
        // be compared line for line.
        output.WriteLine("  .deps.json files found:      " + isolation.DepsFilesFound);
        output.WriteLine("  private resolvers:           " + isolation.ResolverCount);
        output.WriteLine("  privately resolved requests: " + isolation.PrivateResolutionCount);
        if (isolation.OrphanedDeps.Count > 0)
        {
            output.WriteLine("  orphaned deps:               " + string.Join(", ", isolation.OrphanedDeps));
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
                output.WriteLine("  - " + warning);
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
