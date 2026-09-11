namespace Enlist.ControlPlane.Contracts;

/// <summary>
/// An agent name is a route value, a SignalR group name, a container label and a directory name on
/// the control plane's blob store — the same hazards as an application name, so the same rule:
/// see <see cref="ApplicationNames"/>. Applied at enrollment, which is the one place a name enters the
/// system under authentication (Authentication-Design.md section 4).
/// </summary>
public static class AgentNames
{
    public static bool IsValid(string? name) => ApplicationNames.IsValid(name);

    public static string Requirement => $"an agent name is 1-{ApplicationNames.MaxLength} characters of letters, digits, '-', '_' and '.', starting with a letter or digit and not a reserved device name";
}

/// <summary>Constants shared by the control plane, the portal, the CLI and tests, so a role is never a magic string in three places.</summary>
public static class ManagementRoles
{
    public const string Operator = "Operator";
    public const string Viewer = "Viewer";

    public static bool IsValid(string? role) => role is Operator or Viewer;
}

/// <summary>The body an agent sends with a join token to receive its own credential — Authentication-Design.md section 4.1.</summary>
public sealed record EnrollAgentRequest(string AgentName);

/// <summary>Returned once. The agent token is not recoverable afterwards; the control plane keeps only its hash.</summary>
public sealed record EnrollAgentResponse(string AgentName, string AgentToken);
