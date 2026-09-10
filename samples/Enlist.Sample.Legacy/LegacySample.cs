using Enlist;

namespace Enlist.Sample.Legacy;

/// <summary>
/// The net472 counterpart to samples/Enlist.Sample.Service — proves the legacy runner
/// (src/Enlist.Runner.Legacy) end to end against a real .NET Framework 4.7.2 plugin, the same way
/// Enlist.Sample.Service proves the modern runner. Its
/// assembly-level [EnlistApplication] lives in AssemblyInfo.cs, not here — assembly attributes must
/// precede any namespace declaration in their file.
/// </summary>
[EnlistService("Legacy Service", Description = "Logs a heartbeat until stopped - runs on .NET Framework 4.7.2.")]
public sealed class LegacyService
{
    private Task? _loop;

    [EnlistStart]
    public Task Start(CancellationToken stopping, Action<string> log)
    {
        _loop = RunAsync(stopping, log);
        return Task.CompletedTask;
    }

    [EnlistStop]
    public void Stop()
    {
        Console.WriteLine("Stop() called - the loop's own CancellationToken already handles the rest.");
    }

    private static async Task RunAsync(CancellationToken stopping, Action<string> log)
    {
        var tick = 0;
        while (!stopping.IsCancellationRequested)
        {
            log($"heartbeat {++tick} from net472");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

[EnlistJob("Legacy Job", Description = "Runs once and reports its settings - runs on .NET Framework 4.7.2.", Cron = "0 0 2 * * ?")]
public sealed class LegacyJob
{
    [EnlistExecute]
    public void Run(CancellationToken cancelling, IDictionary<string, string> settings)
    {
        Console.WriteLine($"running with {settings.Count} setting(s) on net472");
    }
}

/// <summary>
/// The net472 counterpart to OrderProcessor's Ledger Rebuild — the same cancellation contract, under
/// a runner that shares no assembly with the modern one. See that job for why the check sits where it
/// does.
/// </summary>
[EnlistJob("Legacy Batch", Description = "Long-running batch that stops cleanly between chunks when cancelled - runs on .NET Framework 4.7.2.", Cron = "0 0 6 * * ?")]
public sealed class LegacyBatch
{
    [EnlistExecute]
    public void Run(CancellationToken cancelling, IDictionary<string, string> settings)
    {
        var chunks = settings.TryGetValue("chunks", out var raw) && int.TryParse(raw, out var n) ? n : 20;

        for (var chunk = 1; chunk <= chunks; chunk++)
        {
            if (cancelling.IsCancellationRequested)
            {
                Console.WriteLine($"Legacy batch cancelled after {chunk - 1}/{chunks} chunks.");
                return;
            }

            Console.WriteLine($"Legacy batch chunk {chunk}/{chunks} done.");
            Thread.Sleep(500);
        }

        Console.WriteLine("Legacy batch complete.");
    }
}

/// <summary>Lifecycle methods on a base class, on .NET Framework — discovery walks the hierarchy on both runners.</summary>
public abstract class LegacyHeartbeatBase
{
    private Task? _loop;

    protected abstract string Label { get; }

    [EnlistStart]
    public Task Start(CancellationToken stopping, Action<string> log)
    {
        _loop = RunAsync(stopping, log);
        return Task.CompletedTask;
    }

    [EnlistStop]
    public void Stop() => Console.WriteLine($"{Label}: Stop() called.");

    private async Task RunAsync(CancellationToken stopping, Action<string> log)
    {
        var tick = 0;
        while (!stopping.IsCancellationRequested)
        {
            log($"heartbeat {++tick} from {Label}");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

[EnlistService("Inherited Legacy Service", Description = "Its [EnlistStart]/[EnlistStop] live on a base class - runs on .NET Framework 4.7.2.")]
public sealed class InheritedLegacyService : LegacyHeartbeatBase
{
    protected override string Label => "the inherited legacy service";
}
