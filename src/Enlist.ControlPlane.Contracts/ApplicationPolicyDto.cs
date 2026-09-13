namespace Enlist.ControlPlane.Contracts;

/// <summary>
/// Shared between Enlist.ControlPlane (server) and Enlist.Agent (client) — unlike the runner/plugin
/// boundary, there is no reason to avoid a shared compiled assembly here: both sides are parts of the
/// same system we control end-to-end, so a strongly-typed contract avoids DTO drift instead of
/// creating the version-collision problem the plugin contract is designed around.
///
/// A rule that decides which agents currently qualify to run one application — not a static pairing.
/// TagSelector is the ONLY targeting mechanism: every agent whose tags (see AgentDto) contain all of
/// the selector's key/value pairs qualifies, resolved at query time by the control plane. An empty
/// selector matches every agent. "Target one specific agent" is just the most specific possible
/// selector: every agent implicitly carries its own name as a tag (key "agent"), so
/// {"agent": "DEMO-01"} is how a rule targets exactly one agent — there is no second
/// mechanism competing with tags anymore.
///
/// Path / PackageDigest decide WHAT runs — a pre-existing local folder on the agent, or a
/// content-addressed package the agent downloads and extracts itself (see Enlist.Agent's
/// PackageCache). Exactly one of the two is expected to be set; "neither" or "both" is invalid.
///
/// ConflictReason is resolution OUTPUT, never stored input — see the control plane's
/// ResolveEffectivePoliciesForAgentAsync. Non-null means two or more rules matched this agent for this
/// application and disagreed on DesiredState/Path/PackageDigest/CronOverrides; Path/PackageDigest on
/// this same record are then meaningless (there was no single winner to read them from) and the agent
/// goes straight to Failed with this text rather than guessing which rule should win — the same
/// "fail loud on a real configuration problem" philosophy already used for a missing runtime flavor.
/// Always null on a plain CRUD row (GET/POST/PUT against /api/application-policies).
///
/// Parameter order is append-only across every change made to these records — several existing call
/// sites construct them positionally, and reordering (as opposed to widening a type or appending a
/// new trailing optional parameter) would silently misassign values at those sites instead of failing
/// to compile.
/// </summary>
public sealed record ApplicationPolicyDto(
    Guid Id,
    string ApplicationName,
    string? Path,
    string DesiredState,
    IReadOnlyDictionary<string, string> CronOverrides,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyDictionary<string, string> TagSelector,
    string? PackageDigest = null,
    string RuntimeFlavor = RuntimeFlavors.Default,
    string? ConflictReason = null,
    IsolationSpec? Isolation = null);

public sealed record CreateApplicationPolicyRequest(
    string ApplicationName,
    string? Path,
    string DesiredState,
    IReadOnlyDictionary<string, string>? CronOverrides,
    IReadOnlyDictionary<string, string>? TagSelector = null,
    string? PackageDigest = null,
    string? RuntimeFlavor = null,
    IsolationSpec? Isolation = null);

/// <summary>Targeting (TagSelector) is set at creation and not revisited here — changing WHICH agents qualify is a recreate, not an update. RuntimeFlavor is different: unlike targeting, a redeploy legitimately can migrate an application off a legacy runtime (or onto one), so it follows the same idempotent create-or-update path as Path/PackageDigest rather than being create-only.</summary>
public sealed record UpdateApplicationPolicyRequest(
    string? Path,
    string? DesiredState,
    IReadOnlyDictionary<string, string>? CronOverrides,
    string? PackageDigest = null,
    string? RuntimeFlavor = null,
    IsolationSpec? Isolation = null);

/// <summary>
/// Which enlist-runner build an application policy needs — see docs/03-architecture/SAD.md §9 ("A
/// legacy runner: net472, same attribute contract, no shared assembly"). An agent that doesn't have a
/// runner-bin configured for a given policy's flavor cannot
/// start it — see Enlist.Agent's AgentHost / --legacy-runner-bin. Defined once here, shared by the
/// control plane (validation), the agent (routing to the right runner-bin), and enlist-deploy's
/// upload-time auto-detection, so all three agree on the exact string values without duplicating them.
/// </summary>
public static class RuntimeFlavors
{
    /// <summary>The default — every policy created without specifying one gets this, so nothing already deployed changes behavior.</summary>
    public const string Default = "net10.0";

    public const string NetFramework472 = "net472";

    public static readonly IReadOnlyList<string> All = [Default, NetFramework472];
}

/// <summary>Values of ApplicationPolicyDto.DesiredState. Strings on the wire, like RuntimeFlavors and for the same reason; the constants keep the control plane and the portal from each spelling them.</summary>
public static class DesiredStates
{
    public const string Running = "Running";

    public const string Stopped = "Stopped";

    public static readonly IReadOnlyList<string> All = [Running, Stopped];
}

/// <summary>Method/group-join names for the SignalR hub — kept here so client and server can't drift on the string literals.</summary>
public static class ApplicationPolicyHubContract
{
    public const string HubPath = "/hubs/application-policies";
    public const string JoinAgentGroupMethod = "JoinAgentGroup";
    public const string ApplicationPoliciesChangedMethod = "ApplicationPoliciesChanged";

    /// <summary>Server -> one agent, carrying an AgentCommandRequest payload — see that type for why this doesn't go through the policy reconcile path.</summary>
    public const string ExecuteCommandMethod = "ExecuteCommand";
}
