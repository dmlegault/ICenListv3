using System.Collections.Concurrent;

namespace Enlist.Portal.Services;

/// <summary>
/// One fetch of an agent's latest report per circuit per short interval, however many panels ask.
/// Every expanded application card polls /report/latest for each of its agents every three seconds,
/// and a search that expands twenty cards on two agents was forty requests per tick; through this it
/// is two. Scoped to the circuit (one operator's tab), so no tab ever sees another's staleness.
/// </summary>
public sealed class AgentReportCache
{
    private readonly Func<string, CancellationToken, Task<AgentStatusSnapshotDto?>> _fetch;
    private readonly TimeSpan _freshFor;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public AgentReportCache(ControlPlaneApiClient api)
        : this(api.GetLatestReportAsync, TimeSpan.FromSeconds(2), TimeProvider.System)
    {
    }

    /// <param name="freshFor">How long one fetch answers for. Under the panels' three-second poll, two seconds means every tick fetches once per agent and never twice.</param>
    public AgentReportCache(Func<string, CancellationToken, Task<AgentStatusSnapshotDto?>> fetch, TimeSpan freshFor, TimeProvider time)
    {
        _fetch = fetch;
        _freshFor = freshFor;
        _time = time;
    }

    /// <summary>A report no older than the freshness window — fetched once, shared by every caller that asks meanwhile. A fetch that fails fails for all of them, exactly as their own call would have.</summary>
    public Task<AgentStatusSnapshotDto?> GetLatestAsync(string agentName, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();

        while (true)
        {
            var entry = _entries.GetOrAdd(agentName, name => new Entry(now, _fetch(name, CancellationToken.None)));
            if (now - entry.StartedAt < _freshFor)
            {
                return entry.Result.WaitAsync(ct);
            }

            // Stale: replace it, unless someone else just did — then the loop picks up theirs.
            _entries.TryUpdate(agentName, new Entry(now, _fetch(agentName, CancellationToken.None)), entry);
        }
    }

    private sealed record Entry(DateTimeOffset StartedAt, Task<AgentStatusSnapshotDto?> Result);
}
