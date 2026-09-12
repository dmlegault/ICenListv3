using Enlist.Agent.Logging;

namespace Enlist.Agent.Tests;

public sealed class AgentFileLogSinkTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enlist-logsink-tests-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Concurrent_writes_to_one_log_file_lose_no_lines()
    {
        var sink = new AgentFileLogSink(_root);

        // These all land in the SAME file. The agent log is partitioned by date, not by writer, and
        // every context in AgentHost writes it: the reconciliation loop, crash retries on the thread
        // pool, Process.Exited, the hub command handler, and one receive loop per running
        // application. File.AppendAllTextAsync opens with FileShare.Read, so before these writes were
        // serialized the overlapping ones threw a sharing violation - which the sink swallowed by
        // design, and the line was simply gone. Silently, and worst under load.
        const int lines = 300;
        await Task.WhenAll(Enumerable.Range(0, lines).Select(i => sink.WriteAgentLogAsync($"line-{i:D3}")));

        // Every agent-*.log, not today's by name: a run that straddles midnight would otherwise lose
        // half its lines to a second file and fail for a reason that has nothing to do with the point.
        var written = Directory.EnumerateFiles(_root, "agent-*.log", SearchOption.TopDirectoryOnly)
            .SelectMany(File.ReadAllLines)
            .ToList();

        Assert.Equal(lines, written.Count);
        for (var i = 0; i < lines; i++)
        {
            var expected = $"line-{i:D3}";
            Assert.True(written.Any(l => l.EndsWith(expected, StringComparison.Ordinal)), $"'{expected}' never reached the file.");
        }
    }

    [Fact]
    public async Task PruneOldLogsAsync_deletes_files_older_than_retention_and_keeps_fresh_ones()
    {
        var sink = new AgentFileLogSink(_root);

        var oldFile = Path.Combine(_root, "SomeApp", "2020-01-01.log");
        var freshFile = Path.Combine(_root, "SomeApp", "fresh.log");
        Directory.CreateDirectory(Path.GetDirectoryName(oldFile)!);
        await File.WriteAllTextAsync(oldFile, "old");
        await File.WriteAllTextAsync(freshFile, "fresh");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow - TimeSpan.FromDays(30));
        File.SetLastWriteTimeUtc(freshFile, DateTime.UtcNow);

        await sink.PruneOldLogsAsync(TimeSpan.FromDays(14));

        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(freshFile));
    }

    [Fact]
    public async Task WriteAgentLogAsync_partitions_by_date_so_retention_can_actually_apply_to_it()
    {
        var sink = new AgentFileLogSink(_root);
        await sink.WriteAgentLogAsync("hello");

        var expected = Path.Combine(_root, $"agent-{DateTime.UtcNow:yyyy-MM-dd}.log");
        Assert.True(File.Exists(expected));
    }
}
