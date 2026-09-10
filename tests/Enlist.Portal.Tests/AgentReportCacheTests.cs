using Enlist.Portal.Services;

namespace Enlist.Portal.Tests;

/// <summary>The fan-out limiter behind every panel that polls an agent's report: one fetch per agent per window, however many callers.</summary>
public sealed class AgentReportCacheTests
{
    [Fact]
    public async Task Callers_inside_the_window_share_one_fetch()
    {
        var fetches = 0;
        var gate = new TaskCompletionSource<AgentStatusSnapshotDto?>();
        var time = new ManualTime();
        var cache = new AgentReportCache((_, _) => { Interlocked.Increment(ref fetches); return gate.Task; }, TimeSpan.FromSeconds(2), time);

        var first = cache.GetLatestAsync("DEV-01");
        var second = cache.GetLatestAsync("DEV-01");
        var third = cache.GetLatestAsync("dev-01");
        gate.SetResult(null);
        await Task.WhenAll(first, second, third);

        Assert.Equal(1, fetches);
    }

    [Fact]
    public async Task A_caller_after_the_window_gets_a_fresh_fetch()
    {
        var fetches = 0;
        var time = new ManualTime();
        var cache = new AgentReportCache((_, _) => { Interlocked.Increment(ref fetches); return Task.FromResult<AgentStatusSnapshotDto?>(null); }, TimeSpan.FromSeconds(2), time);

        await cache.GetLatestAsync("DEV-01");
        time.Advance(TimeSpan.FromSeconds(1));
        await cache.GetLatestAsync("DEV-01");
        Assert.Equal(1, fetches);

        time.Advance(TimeSpan.FromSeconds(2));
        await cache.GetLatestAsync("DEV-01");
        Assert.Equal(2, fetches);
    }

    [Fact]
    public async Task Different_agents_are_fetched_separately()
    {
        var asked = new List<string>();
        var cache = new AgentReportCache((name, _) => { lock (asked) { asked.Add(name); } return Task.FromResult<AgentStatusSnapshotDto?>(null); }, TimeSpan.FromSeconds(2), new ManualTime());

        await cache.GetLatestAsync("DEV-01");
        await cache.GetLatestAsync("DEV-02");

        Assert.Equal(new[] { "DEV-01", "DEV-02" }, asked);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
