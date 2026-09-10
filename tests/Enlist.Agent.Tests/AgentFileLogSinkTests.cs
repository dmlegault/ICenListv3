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
