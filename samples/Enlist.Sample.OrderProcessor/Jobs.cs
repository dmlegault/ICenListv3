using Enlist;

namespace Enlist.Sample.OrderProcessor;

[EnlistJob("Nightly Reconciliation", Description = "Reconciles processed orders against payment gateway settlement reports.", Cron = "0 0 3 * * ?")]
public sealed class NightlyReconciliation
{
    [EnlistExecute]
    public void Run(CancellationToken cancelling, IDictionary<string, string> settings)
    {
        Console.WriteLine("Reconciling orders against settlement reports...");
        Console.WriteLine("Reconciliation complete - 0 discrepancies found.");
    }
}

[EnlistJob("Abandoned Cart Sweep", Description = "Finds carts inactive for 24h and sends a reminder.", Cron = "0 */30 * * * ?")]
public sealed class AbandonedCartSweep
{
    [EnlistExecute]
    public void Run(CancellationToken cancelling, IDictionary<string, string> settings)
    {
        Console.WriteLine("Scanning for carts inactive over 24h...");
        Console.WriteLine("Sweep complete - 0 reminders queued.");
    }
}

[EnlistJob("Tax Rate Refresh", Description = "Pulls the latest regional tax tables.", Cron = "0 0 1 * * ?")]
public sealed class TaxRateRefresh
{
    [EnlistExecute]
    public void Run(CancellationToken cancelling, IDictionary<string, string> settings)
    {
        Console.WriteLine("Fetching latest regional tax tables...");
        Console.WriteLine("Tax rates refreshed.");
    }
}

[EnlistJob("Order Archive Purge", Description = "Deletes archived orders past the retention window.", Cron = "0 0 4 * * SUN")]
public sealed class OrderArchivePurge
{
    [EnlistExecute]
    public void Run(CancellationToken cancelling, IDictionary<string, string> settings)
    {
        Console.WriteLine("Purging archived orders past retention...");
        Console.WriteLine("Purge complete - 0 orders removed.");
    }
}

/// <summary>
/// A long-running batch, and the sample that shows what a job is expected to do with the
/// CancellationToken it is handed: check it between units of work and leave cleanly when it trips.
///
/// Two different things can stop this job, and only one of them is the plugin's problem. The agent's
/// scheduler stop unregisters the job so it stops FIRING again, and needs nothing from the code
/// below. Stopping it while a run is underway cancels that run's token — and nothing can make the
/// work stop there except the job noticing, because the runner has no safe way to halt a thread it
/// does not own.
/// </summary>
[EnlistJob("Ledger Rebuild", Description = "Long-running batch - replays the ledger in chunks and stops cleanly between chunks when cancelled.", Cron = "0 0 5 * * ?")]
public sealed class LedgerRebuild
{
    [EnlistExecute]
    public void Run(CancellationToken cancelling, IDictionary<string, string> settings)
    {
        var chunks = settings.TryGetValue("chunks", out var raw) && int.TryParse(raw, out var n) ? n : 20;

        for (var chunk = 1; chunk <= chunks; chunk++)
        {
            // The chunk boundary is where stopping is safe, and picking that point is the job's call,
            // not the runner's — a half-replayed chunk is exactly what this check exists to avoid.
            // Check often enough that a stop feels immediate, rarely enough that no unit of work is
            // left half-done.
            if (cancelling.IsCancellationRequested)
            {
                Console.WriteLine($"Ledger rebuild cancelled after {chunk - 1}/{chunks} chunks.");
                return;
            }

            Console.WriteLine($"Ledger chunk {chunk}/{chunks} replayed.");
            Thread.Sleep(500);
        }

        Console.WriteLine("Ledger rebuild complete.");
    }
}
