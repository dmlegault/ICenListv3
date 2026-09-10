namespace Enlist.Runner.Protocol;

/// <summary>
/// The version of the agent↔runner wire contract — the message shapes in RunnerMessage.cs and
/// AgentCommand.cs, and the newline-delimited JSON framing MessageChannel applies to them.
///
/// This exists for a situation that cannot arise yet. Today the runner binary is staged from a
/// directory the agent itself was deployed with, so the two literally cannot disagree. It matters the
/// moment a runner is distributed separately from the agent — a container image being the case this
/// was added for (docs/03-architecture/Container-Story.md §6.6) — because then an agent can be handed a runner built
/// against a different contract, and the failure without a version check is obscure and late: a
/// missing field deserializes to null or a default, and the application misbehaves somewhere far from
/// the actual cause.
///
/// Bump <see cref="Version"/> whenever a change would make an older peer misinterpret a message:
/// removing or renaming a field, changing a field's meaning or type, or adding a field the receiver
/// must act on. Adding a purely optional field that an older peer can safely ignore does not need a
/// bump.
/// </summary>
public static class RunnerProtocol
{
    /// <summary>
    /// Version 1 is the contract as it shipped before versioning existed — this constant names what
    /// was already there rather than introducing a new wire format, so no behaviour changes on the day
    /// it is added.
    ///
    /// Deliberately still 1 after <c>JobResultMessage.Success</c> (a bool) became <c>Outcome</c> (a
    /// <see cref="JobOutcome"/>): every peer in this repository — both runners, the agent, and the
    /// container image — is rebuilt together, so no v1-shaped peer survives the change. Bump this the
    /// moment that stops being true, because the failure is silent: enums travel as numbers and a
    /// missing property deserializes to its default, so an old runner's <c>"success": false</c> reads
    /// as <c>Outcome = Succeeded</c> — a failed run reported as a clean one.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// What <see cref="ReadyMessage.ProtocolVersion"/> holds when the runner never sent one: the field
    /// is absent from the JSON and System.Text.Json falls back to the record parameter's default. Any
    /// runner built before versioning reports this, and it is reported distinctly from a genuine
    /// mismatch because "too old to say" is a more useful diagnosis than "said 0".
    /// </summary>
    public const int Unreported = 0;

    /// <summary>
    /// Null when the version is acceptable; otherwise a human-readable reason the application cannot
    /// be started, phrased for the agent log an operator actually reads. Kept here beside the constants
    /// so the agent never hand-rolls the comparison and the wording stays in one place.
    /// </summary>
    public static string? DescribeMismatch(int reported) => reported switch
    {
        Version => null,
        Unreported =>
            $"its runner did not report a protocol version, which means it was built before protocol " +
            $"versioning existed (this agent speaks version {Version}). Use a runner built from the same " +
            "enList release as this agent.",
        _ =>
            $"its runner speaks protocol version {reported}, but this agent speaks version {Version}. " +
            "Use a runner built from the same enList release as this agent.",
    };
}
