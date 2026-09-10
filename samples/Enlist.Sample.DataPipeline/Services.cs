using Enlist;

namespace Enlist.Sample.DataPipeline;

[EnlistService("Ingest Listener", Description = "Accepts incoming raw events from upstream producers.")]
public sealed class IngestListener
{
    private Task? _loop;

    [EnlistStart]
    public Task Start(CancellationToken stopping, Action<string> log)
    {
        _loop = RunAsync(stopping, log);
        return Task.CompletedTask;
    }

    [EnlistStop]
    public void Stop() => Console.WriteLine("Ingest Listener stopping.");

    private static async Task RunAsync(CancellationToken stopping, Action<string> log)
    {
        var tick = 0;
        while (!stopping.IsCancellationRequested)
        {
            log($"events accepted: {++tick * 137}");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

[EnlistService("Transform Worker", Description = "Normalizes and validates raw events into the canonical schema.")]
public sealed class TransformWorker
{
    private Task? _loop;

    [EnlistStart]
    public Task Start(CancellationToken stopping, Action<string> log)
    {
        _loop = RunAsync(stopping, log);
        return Task.CompletedTask;
    }

    [EnlistStop]
    public void Stop() => Console.WriteLine("Transform Worker stopping.");

    private static async Task RunAsync(CancellationToken stopping, Action<string> log)
    {
        var tick = 0;
        while (!stopping.IsCancellationRequested)
        {
            log($"batch {++tick} normalized");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

[EnlistService("Enrichment Worker", Description = "Attaches reference/lookup data to each event.")]
public sealed class EnrichmentWorker
{
    private Task? _loop;

    [EnlistStart]
    public Task Start(CancellationToken stopping, Action<string> log)
    {
        _loop = RunAsync(stopping, log);
        return Task.CompletedTask;
    }

    [EnlistStop]
    public void Stop() => Console.WriteLine("Enrichment Worker stopping.");

    private static async Task RunAsync(CancellationToken stopping, Action<string> log)
    {
        var tick = 0;
        while (!stopping.IsCancellationRequested)
        {
            log($"lookup cache hit rate: {90 + (++tick % 10)}%");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(4), stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

[EnlistService("Load Publisher", Description = "Publishes enriched events to the downstream warehouse sink.")]
public sealed class LoadPublisher
{
    private Task? _loop;

    [EnlistStart]
    public Task Start(CancellationToken stopping, Action<string> log)
    {
        _loop = RunAsync(stopping, log);
        return Task.CompletedTask;
    }

    [EnlistStop]
    public void Stop() => Console.WriteLine("Load Publisher stopping.");

    private static async Task RunAsync(CancellationToken stopping, Action<string> log)
    {
        var tick = 0;
        while (!stopping.IsCancellationRequested)
        {
            log($"published batch {++tick} to warehouse sink");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

[EnlistService("Metrics Exporter", Description = "Emits pipeline throughput and lag metrics.")]
public sealed class MetricsExporter
{
    private Task? _loop;

    [EnlistStart]
    public Task Start(CancellationToken stopping, Action<string> log)
    {
        _loop = RunAsync(stopping, log);
        return Task.CompletedTask;
    }

    [EnlistStop]
    public void Stop() => Console.WriteLine("Metrics Exporter stopping.");

    private static async Task RunAsync(CancellationToken stopping, Action<string> log)
    {
        var tick = 0;
        while (!stopping.IsCancellationRequested)
        {
            log($"lag: {++tick % 7}s, throughput: {200 + tick}/s");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(6), stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
