using Enlist.ControlPlane.Contracts;

namespace Enlist.ControlPlane.Data;

/// <summary>
/// One rule deciding which agents currently qualify to run one application — the SQL-backed
/// replacement for the local assignments.json file an AgentHost can read instead. Shape deliberately
/// mirrors Enlist.Agent's own ApplicationAssignment closely: same fields, just now scoped to whichever
/// agents this rule matches and living in a database every agent can be pointed at instead of a file
/// that has to be hand-copied to each one.
/// </summary>
public sealed class ApplicationPolicyEntity
{
    public Guid Id { get; set; }

    /// <summary>The ONLY targeting mechanism — matches every agent whose tags (AgentEntity.TagsJson, plus the implicit self-tag "agent"=&lt;its own name&gt; every agent carries) contain ALL of these key/value pairs. "{}" (empty object) matches every agent. Resolved at query time (see Program.cs's policy-resolution endpoint), not materialized into per-agent rows, so an agent that starts matching later (new agent, a tag added, or a rename) picks this up on its very next fetch with no other action needed.</summary>
    public string TagSelectorJson { get; set; } = "{}";

    public required string ApplicationName { get; set; }

    /// <summary>Alternative to PackageDigest, not both — a pre-existing local folder on that specific agent (the path-based form, which predates package distribution). Nullable now that package distribution exists.</summary>
    public string? Path { get; set; }

    /// <summary>Alternative to Path — a content-addressed package (see PackageEntity) the agent downloads and extracts itself. Exactly one of Path/PackageDigest is expected to be set; enforced at the API layer, not the database (a CHECK constraint would need per-provider syntax EF Core's fluent API doesn't cover portably, and this is a single "if" at the one place rows are written).</summary>
    public string? PackageDigest { get; set; }

    /// <summary>Stored as text ("Running"/"Stopped") rather than an int enum column — readable directly in the database without a lookup table, and matches how the local file already represents it.</summary>
    public required string DesiredState { get; set; }

    /// <summary>Job name -> cron expression, serialized as a JSON object. A dedicated table would be more "correct" relationally, but this is small, always read/written as a whole, and never queried by individual key — not worth the join.</summary>
    public string CronOverridesJson { get; set; } = "{}";

    public DateTimeOffset UpdatedAtUtc { get; set; }

    /// <summary>Which enlist-runner build this application needs (see RuntimeFlavors) — an agent with no runner-bin configured for this value cannot start it. Defaults to RuntimeFlavors.Default so every rule that existed before this concept did keeps behaving exactly as it always has.</summary>
    public string RuntimeFlavor { get; set; } = RuntimeFlavors.Default;

    /// <summary>
    /// A serialized IsolationSpec — HOW this runs (in-process or in a container), as opposed to
    /// RuntimeFlavor's WHAT it needs to run at all. The two are independent axes; see IsolationSpec.
    ///
    /// NULL means process isolation, identical in meaning to a stored {"mode":"process"}: every rule
    /// that never specifies one behaves identically.
    ///
    /// One JSON column rather than several typed ones, matching TagSelectorJson and CronOverridesJson
    /// above and for the same stated reason — always read and written as a whole, never queried by
    /// individual key. The payoff: port mappings and networks are added by filling in fields
    /// of the spec, needing no migration and no change to this entity.
    /// </summary>
    public string? IsolationJson { get; set; }
}
