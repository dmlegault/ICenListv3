namespace Enlist.Agent.Logging;

/// <summary>
/// Turns a stream of best-effort failures into the few lines an operator can actually read: the
/// first failure at once, then at most one line per window while it keeps failing, then the recovery.
/// A status POST that fails every two minutes for an hour is thirty identical lines nobody reads, or
/// it is "failed", "still failing, 12 so far" and "succeeded again after 30 failures" — the second is
/// what this produces. Thread-safe; one instance per thing being attempted.
/// </summary>
public sealed class FailureNotice
{
    private readonly string _what;
    private readonly TimeSpan _window;
    private readonly object _lock = new();
    private int _failures;
    private DateTimeOffset _lastNoticeUtc;

    /// <param name="what">What was being attempted, as the start of a sentence — "Status report to the control plane".</param>
    /// <param name="window">How long to stay quiet after a notice while the failures continue.</param>
    public FailureNotice(string what, TimeSpan window)
    {
        _what = what;
        _window = window;
    }

    /// <summary>The line to log for this failure, or null when it falls inside the quiet window of the last one.</summary>
    public string? Failed(string detail)
    {
        lock (_lock)
        {
            _failures++;
            var now = DateTimeOffset.UtcNow;
            if (_failures > 1 && now - _lastNoticeUtc < _window)
            {
                return null;
            }

            _lastNoticeUtc = now;
            return _failures == 1
                ? $"{_what} failed: {detail}"
                : $"{_what} is still failing ({_failures} in a row): {detail} - said at most once per {_window.TotalMinutes:0} minutes while it lasts.";
        }
    }

    /// <summary>The line to log when a success follows failures, or null when nothing was wrong.</summary>
    public string? Succeeded()
    {
        lock (_lock)
        {
            if (_failures == 0)
            {
                return null;
            }

            var failures = _failures;
            _failures = 0;
            return $"{_what} succeeded again after {failures} failure(s).";
        }
    }
}
