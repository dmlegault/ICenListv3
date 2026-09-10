using Enlist;

namespace Enlist.Sample.DataPipeline;

[EnlistJob("Daily Partition Rollup", Description = "Compacts the day's hourly partitions into one.", Cron = "0 15 0 * * ?")]
public sealed class DailyPartitionRollup
{
    [EnlistExecute]
    public void Run(CancellationToken cancelling, IDictionary<string, string> settings)
    {
        Console.WriteLine("Compacting hourly partitions...");
        Console.WriteLine("Rollup complete.");
    }
}

[EnlistJob("Schema Drift Check", Description = "Compares incoming event shapes against the registered schema.", Cron = "0 0 */6 * * ?")]
public sealed class SchemaDriftCheck
{
    [EnlistExecute]
    public void Run(CancellationToken cancelling, IDictionary<string, string> settings)
    {
        Console.WriteLine("Comparing recent event shapes against registered schema...");
        Console.WriteLine("No drift detected.");
    }
}

[EnlistJob("Stale Checkpoint Cleanup", Description = "Removes checkpoint files older than 7 days.", Cron = "0 30 2 * * ?")]
public sealed class StaleCheckpointCleanup
{
    [EnlistExecute]
    public void Run(CancellationToken cancelling, IDictionary<string, string> settings)
    {
        Console.WriteLine("Scanning for checkpoint files older than 7 days...");
        Console.WriteLine("Cleanup complete - 0 files removed.");
    }
}
