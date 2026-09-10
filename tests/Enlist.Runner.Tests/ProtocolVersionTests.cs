using System.Text.Json;

using Enlist.Runner.Protocol;

namespace Enlist.Runner.Tests;

/// <summary>
/// docs/03-architecture/Container-Story.md §6.6 — the agent must be able to tell a runner that speaks its
/// protocol from one that does not, before acting on anything the runner said.
///
/// These are unit tests of the comparison and — more importantly — of the DESERIALIZATION default,
/// which is the part that is easy to get subtly and invisibly wrong: if a Ready message with no
/// version field defaulted to the current version, every runner too old to send one would silently
/// claim to be current, and the check would pass exactly when it most needed to fail.
/// </summary>
public sealed class ProtocolVersionTests
{
    [Fact]
    public void A_ready_message_without_a_version_field_deserializes_as_unreported_not_as_current()
    {
        // Exactly the shape a pre-versioning runner puts on the wire: no "protocolVersion" property.
        const string json = """
            {"kind":"ready","services":[],"jobs":[],"warnings":[],"isolation":{"depsFilesFound":1,"resolverCount":0,"privateResolutionCount":0,"orphanedDeps":[]}}
            """;

        var ready = Assert.IsType<ReadyMessage>(Deserialize(json));

        Assert.Equal(RunnerProtocol.Unreported, ready.ProtocolVersion);
        Assert.NotEqual(RunnerProtocol.Version, ready.ProtocolVersion);
        Assert.NotNull(RunnerProtocol.DescribeMismatch(ready.ProtocolVersion));
    }

    [Fact]
    public void A_ready_message_round_trips_its_version_through_the_real_serializer()
    {
        var original = new ReadyMessage([], [], [], new IsolationInfo(1, 0, 0, []), "desc", RunnerProtocol.Version);

        // Through JsonOptions.Default's camelCase policy and the polymorphic discriminator, not a
        // hand-written string — a naming-policy change that broke this field would otherwise only show
        // up as a mysterious version mismatch in production.
        var ready = Assert.IsType<ReadyMessage>(Deserialize(Serialize(original)));

        Assert.Equal(RunnerProtocol.Version, ready.ProtocolVersion);
        Assert.Null(RunnerProtocol.DescribeMismatch(ready.ProtocolVersion));
    }

    [Fact]
    public void A_mismatched_version_is_described_distinctly_from_an_absent_one()
    {
        var unreported = RunnerProtocol.DescribeMismatch(RunnerProtocol.Unreported);
        var newer = RunnerProtocol.DescribeMismatch(RunnerProtocol.Version + 1);

        Assert.NotNull(unreported);
        Assert.NotNull(newer);

        // "Too old to report a version" and "reports a different version" are different diagnoses and
        // lead an operator to different fixes, so they must not collapse into one message.
        Assert.NotEqual(unreported, newer);
        Assert.Contains("did not report a protocol version", unreported);
        Assert.Contains((RunnerProtocol.Version + 1).ToString(), newer);
    }

    /// <summary>Mirrors MessageChannel's own serializer settings — the internal JsonOptions.Default isn't visible from here, so this test would be worthless if the two ever drifted; keep them in step.</summary>
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string Serialize(RunnerMessage message) => JsonSerializer.Serialize(message, typeof(RunnerMessage), Options);

    private static RunnerMessage? Deserialize(string json) => JsonSerializer.Deserialize<RunnerMessage>(json, Options);
}
