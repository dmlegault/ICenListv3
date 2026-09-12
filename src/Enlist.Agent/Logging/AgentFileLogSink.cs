using System.Collections.Concurrent;

using Enlist.Runner.Protocol;

namespace Enlist.Agent.Logging;

/// <summary>
/// Persists to disk what the runner ships over the pipe — design doc section 7: "The agent persists
/// to disk and forwards to the control plane." Both halves are here: the file on disk is the durable
/// copy, and every application line is also handed to an ILogForwarder, which mirrors it to the
/// control plane for the portal's log tail. A log write failing must never take the agent down, so
/// every write here swallows its own exceptions rather than letting a full disk cascade into losing a
/// running application.
/// </summary>
public sealed class AgentFileLogSink
{
    private readonly string _logRoot;
    private readonly ILogForwarder _forwarder;

    /// <summary>
    /// One gate per file, because appends to the SAME file genuinely do overlap: an application's
    /// receive loop and a job firing on the scheduler both write that application's log, and every
    /// application plus the agent itself shares the agent log. File.AppendAllTextAsync opens with
    /// FileShare.Read, so two concurrent appends throw a sharing violation — which the catch below
    /// then swallowed, losing the line outright. That is the worst way for a log to fail: silently,
    /// and most often under load, which is exactly when it is being read.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileGates = new(StringComparer.OrdinalIgnoreCase);

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

                        // Its gate goes with it. Files are date-partitioned, so a deleted one is from
                        // a past day and will never be appended to again; without this the dictionary
                        // would grow by one entry per application per day for the life of the process.
                        _fileGates.TryRemove(file, out _);
                    }
                }
                catch
                {
                }
            }
        });
    }

    private async Task AppendAsync(string directory, string fileName, string line)
    {
        var path = Path.Combine(directory, fileName);
        var gate = _fileGates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directory);
            await File.AppendAllTextAsync(path, line).ConfigureAwait(false);
        }
        catch
        {
            // Still swallowed, and still deliberately: a full disk must not stop an application. What
            // changed is that a sharing violation caused by THIS class writing over itself is no
            // longer one of the things being swallowed.
        }
        finally
        {
            gate.Release();
        }
    }
}
