namespace Enlist.Runner.Legacy.Protocol;

/// <summary>
/// The version of the agent↔runner wire contract, duplicated as source from
/// Enlist.Runner/Protocol/RunnerProtocol.cs for the same reason the message types beside it are — this
/// project deliberately shares no assembly with the modern runner (see RunnerMessage.cs).
///
/// **The two copies MUST agree.** <see cref="Version"/> here and in Enlist.Runner are the same wire
/// contract seen from two builds; if one changes, so does the other, in the same commit.
///
/// That duplication has already bitten once. Protocol versioning was added to the modern runner and
/// not to this one, so this runner reported no version at all and the agent — correctly — refused to
/// start any net472 application with "its runner did not report a protocol version". That stayed hidden
/// only because a separate bug meant net472 policies were being routed to the modern runner anyway, so
/// this code path was never exercised. The check worked; the duplication is what let it drift.
/// </summary>
public static class RunnerProtocol
{
    /// <summary>Must equal Enlist.Runner's RunnerProtocol.Version — see that copy for when to bump, and note that leaving this one behind is the exact failure this file's header describes.</summary>
    public const int Version = 2;

    /// <summary>What ReadyMessage.ProtocolVersion holds when a runner never sent one — see the modern copy for the full reasoning about why this is NOT defaulted to Version.</summary>
    public const int Unreported = 0;
}
