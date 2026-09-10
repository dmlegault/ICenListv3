namespace Enlist.ControlPlane.Contracts;

/// <summary>One application log line, as forwarded by an agent and as returned to a portal tailing a machine's logs. Shared (not opaque-JSON like AgentStatusSnapshot) because both ends already need to agree on it to page through results by Id — there's no control-plane-side reason to decouple the shape here.</summary>
public sealed record AgentLogEntryDto(long Id, string ApplicationName, string Level, string Source, string Text, DateTimeOffset TimestampUtc);

/// <summary>Batch submission body — agents forward buffered lines periodically rather than one HTTP call per line (see Enlist.Agent's ControlPlaneLogForwarder).</summary>
public sealed record SubmitLogEntriesRequest(IReadOnlyList<LogEntrySubmission> Entries);

public sealed record LogEntrySubmission(string ApplicationName, string Level, string Source, string Text, DateTimeOffset TimestampUtc);
