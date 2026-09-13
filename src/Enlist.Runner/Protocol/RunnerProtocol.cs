namespace Enlist.Runner.Protocol;

/// <summary>
/// The version of the agent↔runner wire contract — the message shapes in RunnerMessage.cs and
/// AgentCommand.cs, and the newline-delimited JSON framing MessageChannel applies to them.
///
/// This exists for a situation that CAN now arise. A runner staged from the directory the agent was
/// deployed with cannot disagree with it - the two ship as one thing. A runner that arrives as a
/// container image can: the image on a host is whatever was last pulled or built there, and an agent
/// can be handed one built against a different contract (docs/03-architecture/Container-Story.md
/// section 6.6). Without a version check that failure is obscure and late - a missing field
/// deserializes to null or a default, and the application misbehaves somewhere far from the cause.
///
/// Bump <see cref="Version"/> whenever a change would make an older peer misinterpret a message:
/// removing or renaming a field, changing a field's meaning or type, or adding a field the receiver
/// must act on. Adding a purely optional field that an older peer can safely ignore does not need a
/// bump.
/// </summary>
public static class RunnerProtocol
{
    /// <summary>
    /// Version 2. Version 1 was the contract as it shipped before versioning existed; this is the
    /// first bump, and it went in on 2026-09-13 for a change that had already happened without one.
    ///
    /// <c>JobResultMessage.Success</c> (a bool) became <c>Outcome</c> (a <see cref="JobOutcome"/>),
    /// and the version stayed at 1 on the reasoning that every peer in this repository is rebuilt
    /// together so no v1-shaped peer could survive it. That reasoning expired when the runner started
    /// shipping as a container image: an `enlist/runner` image built before the change is a v1-shaped
    /// peer, still on a host, and it reports version 1 while claiming to be current.
    ///
    /// What that costs is worth spelling out, because it is silent. Enums travel as numbers and a
    /// missing property deserializes to its default, so an old runner's <c>"success": false</c> reads
    /// as <c>Outcome = Succeeded</c>: a failed job reported as a clean one, with nothing anywhere
    /// saying otherwise. The bump turns that into a refusal at startup naming both versions.
    ///
    /// A container image built before this bump will now be refused. That is the intended effect and
    /// the remedy is to rebuild it - see docs/02-building-applications/Container-Developer-Guide.md.
    /// </summary>
    public const int Version = 2;

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
