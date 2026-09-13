namespace Enlist.TestSupport;

/// <summary>
/// Wait for something to become true, for tests that drive real processes.
///
/// Nothing in this repository is mocked at the seams: a test starts a real agent, a real control
/// plane, a real runner, and then has to wait for the effect it is asserting on to actually happen.
/// That produced six near-identical poll loops across the test projects, differing only in what they
/// did when the deadline passed and whether the interval was 100 ms or 250 ms. They are one thing
/// now, with the two behaviours named rather than re-implemented:
///
///   - <see cref="UntilAsync(Func{bool}, TimeSpan, string, TimeSpan?)"/> when not happening is the
///     failure. It throws, carrying the caller's own sentence about what did not happen.
///   - <see cref="TryUntilAsync(Func{bool}, TimeSpan, TimeSpan?)"/> when the test has something more
///     specific to say about the timeout than "it never happened" - usually an assertion on the state
///     it found instead.
///
/// A timeout here is the failure report, so the message is the caller's to write: "Condition not met
/// within 30s" tells whoever reads the CI log nothing at all.
///
/// The condition is evaluated once more when the deadline has passed but not before the last
/// interval elapsed, so a condition that becomes true in the final interval is not reported as a
/// timeout - a source of genuinely spurious failures on a loaded build machine.
/// </summary>
public static class Poll
{
    /// <summary>How often the condition is re-evaluated when the caller does not say.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(250);

    public static Task UntilAsync(Func<bool> condition, TimeSpan timeout, string failure, TimeSpan? interval = null) =>
        UntilAsync(() => Task.FromResult(condition()), timeout, failure, interval);

    public static async Task UntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string failure, TimeSpan? interval = null)
    {
        if (!await TryUntilAsync(condition, timeout, interval).ConfigureAwait(false))
        {
            throw new TimeoutException($"{failure} (waited {timeout.TotalSeconds:0}s)");
        }
    }

    public static Task<bool> TryUntilAsync(Func<bool> condition, TimeSpan timeout, TimeSpan? interval = null) =>
        TryUntilAsync(() => Task.FromResult(condition()), timeout, interval);

    public static async Task<bool> TryUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, TimeSpan? interval = null)
    {
        var step = interval ?? DefaultInterval;
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            if (await condition().ConfigureAwait(false))
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(step).ConfigureAwait(false);
        }
    }
}
