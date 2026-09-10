using Enlist;

namespace Enlist.Sample.OrderProcessor;

[EnlistService("Order Intake Listener", Description = "Watches the inbound order queue and enqueues new orders for processing.")]
public sealed class OrderIntakeListener
{
    private Task? _loop;

    [EnlistStart]
    public Task Start(CancellationToken stopping, Action<string> log)
    {
        _loop = RunAsync(stopping, log, "order received (v2)", TimeSpan.FromSeconds(2));
        return Task.CompletedTask;
    }

    [EnlistStop]
    public void Stop() => Console.WriteLine("Order Intake Listener stopping.");

    private static async Task RunAsync(CancellationToken stopping, Action<string> log, string message, TimeSpan interval)
    {
        var tick = 0;
        while (!stopping.IsCancellationRequested)
        {
            log($"{message} #{++tick}");
            try
            {
                await Task.Delay(interval, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

[EnlistService("Inventory Sync Worker", Description = "Keeps local stock levels in sync with the warehouse system.")]
public sealed class InventorySyncWorker
{
    private Task? _loop;

    [EnlistStart]
    public Task Start(CancellationToken stopping, Action<string> log)
    {
        _loop = RunAsync(stopping, log);
        return Task.CompletedTask;
    }

    [EnlistStop]
    public void Stop() => Console.WriteLine("Inventory Sync Worker stopping.");

    private static async Task RunAsync(CancellationToken stopping, Action<string> log)
    {
        var tick = 0;
        while (!stopping.IsCancellationRequested)
        {
            log($"stock levels synced ({++tick} SKUs updated)");
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

[EnlistService("Payment Gateway Bridge", Description = "Relays authorization and capture requests to the payment processor.")]
public sealed class PaymentGatewayBridge
{
    private Task? _loop;

    [EnlistStart]
    public Task Start(CancellationToken stopping, Action<string> log)
    {
        _loop = RunAsync(stopping, log);
        return Task.CompletedTask;
    }

    [EnlistStop]
    public void Stop() => Console.WriteLine("Payment Gateway Bridge stopping.");

    private static async Task RunAsync(CancellationToken stopping, Action<string> log)
    {
        var tick = 0;
        while (!stopping.IsCancellationRequested)
        {
            log($"heartbeat {++tick} - gateway reachable");
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

[EnlistService("Notification Dispatcher", Description = "Sends order-status emails and SMS to customers.")]
public sealed class NotificationDispatcher
{
    private Task? _loop;

    [EnlistStart]
    public Task Start(CancellationToken stopping, Action<string> log)
    {
        _loop = RunAsync(stopping, log);
        return Task.CompletedTask;
    }

    [EnlistStop]
    public void Stop() => Console.WriteLine("Notification Dispatcher stopping.");

    private static async Task RunAsync(CancellationToken stopping, Action<string> log)
    {
        var tick = 0;
        while (!stopping.IsCancellationRequested)
        {
            log($"queue drained ({++tick} notifications sent)");
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
