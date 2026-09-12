using Enlist;

[assembly: EnlistApplication(Description = "Services that fail in specific, deliberate ways, so the runner's failure paths can be tested against a real plugin rather than described in a comment.")]

namespace Enlist.TestPlugins.Misbehaving;

/// <summary>
/// Throws in its CONSTRUCTOR, before any lifecycle method can run. The runner sends
/// StateChanged(Starting) and then calls Activator.CreateInstance, so this is the one failure that
/// happens between "starting" being announced and there being anything to start.
///
/// Until 2026-09-12 the runner answered it with a FaultedMessage and no state change. The agent only
/// LOGS a FaultedMessage, so the service stayed Starting for the life of the runner.
/// </summary>
[EnlistService("Throws In Constructor", Description = "Cannot be constructed at all.")]
public sealed class ThrowsInConstructor
{
    public ThrowsInConstructor() => throw new InvalidOperationException("this service cannot be constructed, on purpose");

    [EnlistStart]
    public void Start()
    {
    }

    [EnlistStop]
    public void Stop()
    {
    }
}

/// <summary>
/// Starts cleanly and then overruns its stop, which is the case a StopServiceCommand's TimeoutMs
/// exists for. The runner stops waiting, reports Stopping a second time and removes the registry
/// entry — and used to drop the still-running invocation entirely, so the eventual completion was
/// never reported and the service sat at Stopping forever.
///
/// The delay is long enough to overrun a short test timeout and short enough that the test does not
/// have to wait out a real one.
/// </summary>
[EnlistService("Slow To Stop", Description = "Takes longer to stop than it is given.")]
public sealed class SlowToStop
{
    public static readonly TimeSpan StopDuration = TimeSpan.FromSeconds(3);

    [EnlistStart]
    public void Start()
    {
    }

    [EnlistStop]
    public async Task StopAsync() => await Task.Delay(StopDuration).ConfigureAwait(false);
}

/// <summary>An ordinary service, so a test can prove one bad neighbour does not take the application with it.</summary>
[EnlistService("Behaves Normally", Description = "Starts and stops without complaint.")]
public sealed class BehavesNormally
{
    [EnlistStart]
    public void Start()
    {
    }

    [EnlistStop]
    public void Stop()
    {
    }
}

/// <summary>A job that reports what settings it was given, for the "settings omitted entirely" case.</summary>
[EnlistJob("Echo Settings", Description = "Writes the settings it received.")]
public sealed class EchoSettings
{
    [EnlistExecute]
    public void Execute(TextWriter log) => log.WriteLine("Echo Settings ran.");
}
