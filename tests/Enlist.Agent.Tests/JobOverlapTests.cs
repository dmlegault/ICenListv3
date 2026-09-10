using Enlist.Agent.Configuration;
using Enlist.Runner.Protocol;
using Enlist.TestSupport;

namespace Enlist.Agent.Tests;

/// <summary>
/// A job slower than its schedule, against a real runner: OrderProcessor's Ledger Rebuild takes ten
/// seconds (20 chunks of 500 ms) and is put on a two-second cron. The ticks that arrive while it is
/// running are skipped and written down in the application's log — the run is never stacked on top of
/// itself, which is what happened before: one more concurrent copy per tick, without bound.
/// </summary>
public sealed class JobOverlapTests : IAsyncDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "enlist-overlap-tests-" + Guid.NewGuid().ToString("N"));
    private AgentHost? _host;

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
        }

        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task A_tick_that_arrives_while_the_previous_run_is_still_going_is_skipped_and_logged()
    {
        var appName = "Overlap" + Guid.NewGuid().ToString("N")[..8];
        var assignments = new AgentAssignments
        {
            Applications = new List<ApplicationAssignment>
            {
                new()
                {
                    Name = appName,
                    Path = RepoPaths.OrderProcessorDir(),
                    CronOverrides = new Dictionary<string, string> { ["Ledger Rebuild"] = "*/2 * * * * *" },
                },
            },
        };
        _host = new AgentHost(assignments, RepoPaths.RunnerBinDirectory(), Path.Combine(_dataRoot, "Runners"), Path.Combine(_dataRoot, "Logs"));

        var firstResult = new TaskCompletionSource<JobResultMessage>();
        _host.MessageReceived += (_, message) =>
        {
            if (message is JobResultMessage { Job: "Ledger Rebuild" } jr)
            {
                firstResult.TrySetResult(jr);
            }
        };

        await _host.StartAsync();

        var result = await firstResult.Task.WaitAsync(TimeSpan.FromSeconds(40));
        Assert.Equal(JobOutcome.Succeeded, result.Outcome);

        // Four ticks (at +2, +4, +6 and +8 s) came due while the ten-second run was going. Before the
        // policy, each of them started another copy — five "chunk 1/20" lines by now. With it, one run
        // (the tick right at the finish may legitimately start a second) and the skips are on record.
        var appLog = ReadAppLog(appName);
        Assert.InRange(CountOf(appLog, "Ledger chunk 1/20"), 1, 2);
        Assert.True(CountOf(appLog, "this tick is skipped") >= 3, "the ticks that arrived mid-run were not written down as skipped:" + Environment.NewLine + appLog);
    }

    private string ReadAppLog(string appName)
    {
        var dir = Path.Combine(_dataRoot, "Logs", appName);
        return Directory.Exists(dir)
            ? string.Join(Environment.NewLine, Directory.GetFiles(dir, "*.log").Select(File.ReadAllText))
            : "";
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
