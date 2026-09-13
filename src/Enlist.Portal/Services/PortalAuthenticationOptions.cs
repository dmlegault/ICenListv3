using Enlist.ControlPlane.Contracts;

namespace Enlist.Portal.Services;

/// <summary>
/// The portal's "Authentication" section (Authentication-Design.md sections 5 and 8). Required unless
/// configuration says Off, and Off is honoured only when every listener is loopback - the same rule,
/// from the same shared <see cref="ListenerRules"/>, as the control plane. Under Required people sign
/// in with Windows (Negotiate: Kerberos in a domain, NTLM against local accounts) and the two group
/// names decide who is an Operator and who is a Viewer; under Off everyone is an Operator, which is
/// the demo on loopback.
///
///   "Authentication": {
///     "Mode": "Required",
///     "Windows": { "OperatorsGroup": "CORP\\enList Operators", "ViewersGroup": "CORP\\enList Viewers" }
///   }
/// </summary>
public sealed class PortalAuthenticationOptions
{
    public const string SectionName = "Authentication";

    public string Mode { get; set; } = AuthenticationModes.Required;

    public WindowsGroups Windows { get; set; } = new();

    public bool IsOff => AuthenticationModes.IsOff(Mode);

    public bool IsRequired => !IsOff;

    public sealed class WindowsGroups
    {
        /// <summary>Members are Operators: every page, every write. A Windows group name, "DOMAIN\Group" or "BUILTIN\Administrators".</summary>
        public string? OperatorsGroup { get; set; }

        /// <summary>Members are Viewers: every page, no write controls. Empty means nobody is a Viewer.</summary>
        public string? ViewersGroup { get; set; }

        /// <summary>
        /// A group name as a screen should show it. One definition, because two screens show these
        /// and they disagreed: the Access page said "not configured" and the access-denied page said
        /// "(not configured - nobody)" for the same empty setting, which reads as two different
        /// states to anyone who sees both.
        ///
        /// The longer wording wins. On the page that tells someone they were refused, the reason
        /// they were refused is the whole message, and "not configured" alone does not say that
        /// nobody at all can get in.
        /// </summary>
        public static string Describe(string? group) =>
            string.IsNullOrWhiteSpace(group) ? "not configured - nobody" : group;
    }
}
