namespace Enlist.Agent.Supervision;

/// <summary>
/// Exponential backoff with a cap, plus a "settled" reset: an application that ran fine for a while
/// before crashing once shouldn't count the same as one crash-looping immediately on every restart.
/// Only RAPID, repeated failures count against MaxAttempts.
/// </summary>
public sealed class CrashBackoff
{
    public int MaxAttempts { get; }
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private readonly TimeSpan _settledDuration;

    public CrashBackoff(int maxAttempts, TimeSpan baseDelay, TimeSpan maxDelay, TimeSpan settledDuration)
    {
        MaxAttempts = maxAttempts;
        _baseDelay = baseDelay;
        _maxDelay = maxDelay;
        _settledDuration = settledDuration;
    }

    public static CrashBackoff Default => new(
        maxAttempts: 5,
        baseDelay: TimeSpan.FromSeconds(2),
        maxDelay: TimeSpan.FromSeconds(60),
        settledDuration: TimeSpan.FromMinutes(2));

    /// <summary>2s, 4s, 8s, 16s, 32s, capped — attempt is 1-based (the first retry after a crash).</summary>
    public TimeSpan DelayFor(int attempt)
    {
        var seconds = _baseDelay.TotalSeconds * Math.Pow(2, Math.Max(0, attempt - 1));
        return TimeSpan.FromSeconds(Math.Min(seconds, _maxDelay.TotalSeconds));
    }

    /// <summary>True if the previous run lasted long enough that a fresh failure shouldn't count against a prior streak.</summary>
    public bool HasSettledSince(DateTimeOffset startedAt, DateTimeOffset now) => now - startedAt > _settledDuration;
}
