namespace Enlist.ControlPlane.Authentication;

/// <summary>The claims a validated credential produces, and everything in <see cref="EndpointPolicies"/> keys on — Authentication-Design.md section 3.</summary>
public static class EnlistClaims
{
    /// <summary>"agent", "management" or "join".</summary>
    public const string Kind = "enlist:kind";

    /// <summary>The agent's name, for agent credentials only. Compared to the route's agent name on every agent-facing call.</summary>
    public const string Agent = "enlist:agent";

    /// <summary>Operator or Viewer, for management credentials only.</summary>
    public const string Role = "enlist:role";

    /// <summary>What to write in an audit line: the agent name, the key name, or the join token id.</summary>
    public const string Credential = "enlist:credential";

    public const string KindAgent = "agent";
    public const string KindManagement = "management";
    public const string KindJoin = "join";
}
