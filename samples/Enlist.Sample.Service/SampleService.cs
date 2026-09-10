using Enlist;

namespace Enlist.Sample.Service;

/// <summary>
/// A minimal working plugin, proving the pipeline end to end the same way enList v2's SampleApp does
/// — one service, one job, zero packages, discoverable purely by attribute.
/// </summary>
[EnlistService("Sample Service", Description = "Logs a heartbeat until stopped.")]
public sealed class SampleService
{
    private Task? _loop;

    [EnlistStart]
    public Task Start(CancellationToken stopping, Action<string> log)
    {
        // Returns promptly, per the design's async-start allowance — the loop itself keeps running,
        // observing the token, until StopServiceCommand cancels it.
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
            log($"heartbeat {++tick}");
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

[EnlistJob("Sample Job", Description = "Runs once and reports its settings.", Cron = "0 0 2 * * ?")]
public sealed class SampleJob
{
    [EnlistExecute]
    public void Run(CancellationToken cancelling, IDictionary<string, string> settings)
    {
        Console.WriteLine($"running with {settings.Count} setting(s):");
        foreach (var (key, value) in settings)
        {
            Console.WriteLine($"  {key} = {value}");
        }
    }
}

/// <summary>
/// Lifecycle methods on a base class — a plugin author's ordinary way to share one start/stop loop
/// across services. Discovery walks the hierarchy, so the derived class needs only its attribute and
/// whatever makes it different.
/// </summary>
public abstract class HeartbeatServiceBase
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

[EnlistService("Inherited Service", Description = "Its [EnlistStart]/[EnlistStop] live on a base class.")]
public sealed class InheritedService : HeartbeatServiceBase
{
    protected override string Label => "the inherited service";
}
