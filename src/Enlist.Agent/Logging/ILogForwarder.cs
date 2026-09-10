using Enlist.Runner.Protocol;

namespace Enlist.Agent.Logging;

/// <summary>Where AgentFileLogSink mirrors app log lines beyond the local file — nothing, by default (NullLogForwarder), or the control plane when one is configured. Same shape as IStatusReporter/IPrunesPackageCache: an interface AgentHost's constructor takes so local-only mode never needs to know the control plane exists.</summary>
public interface ILogForwarder
{
    void Enqueue(string applicationName, LogMessage message);
}

public sealed class NullLogForwarder : ILogForwarder
{
    public static readonly NullLogForwarder Instance = new();

    public void Enqueue(string applicationName, LogMessage message)
    {
    }
}
