using System.Text.Json;

namespace Enlist.Agent.Status;

/// <summary>Writes the status snapshot to disk. A write failing must never take the agent down — same swallow-and-continue posture as AgentFileLogSink.</summary>
public sealed class AgentStatusWriter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <param name="logRoot">The agent's LOG root — AgentHost passes logRoot, so the file lands at &lt;data&gt;/Logs/status.json, not directly under the data root.</param>
    public AgentStatusWriter(string logRoot)
    {
        Directory.CreateDirectory(logRoot);
        _path = Path.Combine(logRoot, "status.json");
    }

    public string FilePath => _path;

    public async Task WriteAsync(AgentStatusSnapshot snapshot)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.Serialize(snapshot, Options);
            await File.WriteAllTextAsync(_path, json).ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            _lock.Release();
        }
    }
}
