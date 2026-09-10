using Enlist.Runner.Protocol;

namespace Enlist.Agent.Logging;

/// <summary>
/// Persists to disk what the runner ships over the pipe — design doc section 7: "The agent persists
/// to disk and forwards to the control plane." Forwarding is the control plane's job (nothing to forward to
/// yet); this is the local half. A log write failing must never take the agent down, so every write
/// here swallows its own exceptions rather than letting a full disk or a sharing violation cascade
/// into losing a running application.
/// </summary>
public sealed class AgentFileLogSink
{
    private readonly string _logRoot;
    private readonly ILogForwarder _forwarder;

    public AgentFileLogSink(string logRoot, ILogForwarder? forwarder = null)
    {
        _logRoot = logRoot;
        _forwarder = forwarder ?? NullLogForwarder.Instance;
        Directory.CreateDirectory(logRoot);
    }

    public async Task WriteAppLogAsync(string applicationName, LogMessage message)
    {
        _forwarder.Enqueue(applicationName, message);

        var dir = Path.Combine(_logRoot, applicationName);
        var line = $"{message.TimestampUtc:O} [{message.Level}] {message.Source}: {message.Text}{Environment.NewLine}";
        await AppendAsync(dir, $"{DateTime.UtcNow:yyyy-MM-dd}.log", line).ConfigureAwait(false);
    }

    public Task WriteAgentLogAsync(string text)
    {
        var line = $"{DateTimeOffset.UtcNow:O} {text}{Environment.NewLine}";
        // Date-partitioned the same way app logs are — a single ever-growing agent.log would be
        // immune to the retention sweep below, which prunes by file age.
        return AppendAsync(_logRoot, $"agent-{DateTime.UtcNow:yyyy-MM-dd}.log", line);
    }

    /// <summary>Deletes log files (agent-level and per-app) whose last write is older than retention. Best-effort — a file that can't be deleted (locked, permission denied) is skipped, not fatal.</summary>
    public Task PruneOldLogsAsync(TimeSpan retention)
    {
        var cutoffUtc = DateTime.UtcNow - retention;
        return Task.Run(() =>
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(_logRoot, "*.log", SearchOption.AllDirectories).ToList();
            }
            catch
            {
                return;
            }

            foreach (var file in files)
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoffUtc)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                }
            }
        });
    }

    private static async Task AppendAsync(string directory, string fileName, string line)
    {
        try
        {
            Directory.CreateDirectory(directory);
            await File.AppendAllTextAsync(Path.Combine(directory, fileName), line).ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
