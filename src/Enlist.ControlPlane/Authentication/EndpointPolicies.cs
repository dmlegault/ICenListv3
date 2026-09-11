using Enlist.ControlPlane.Contracts;

namespace Enlist.ControlPlane.Authentication;

public enum EndpointPolicy
{
    /// <summary>No credential needed. Only /health.</summary>
    Anonymous,

    /// <summary>A live join token. Only enrollment.</summary>
    JoinToken,

    /// <summary>An agent credential whose name equals the route's agent name.</summary>
    Agent,

    /// <summary>Any agent credential, or any management credential — package download.</summary>
    AgentOrViewer,

    /// <summary>Any management credential.</summary>
    Viewer,

    /// <summary>A management credential with the Operator role.</summary>
    Operator,
}

/// <summary>
/// Every endpoint's policy, in one table, keyed by HTTP method and the route template as written —
/// Authentication-Design.md section 7, line for line. Kept as data rather than an attribute per
/// endpoint so the whole authorization surface is reviewable in one screen, and so the default for
/// an endpoint that is NOT in the table is refusal: a new route is denied to everyone until someone
/// decides, here, which of these three sentences it belongs to — reads are Viewer, writes are
/// Operator, agents act as themselves.
/// </summary>
public static class EndpointPolicies
{
    private static readonly Dictionary<(string Method, string Pattern), EndpointPolicy> Table = new()
    {
        [("GET", "/health")] = EndpointPolicy.Anonymous,

        [("POST", "/api/agents/enroll")] = EndpointPolicy.JoinToken,

        [("GET", "/api/agents/{agentName}/policies")] = EndpointPolicy.Agent,
        [("POST", "/api/agents/{agentName}/report")] = EndpointPolicy.Agent,
        [("POST", "/api/agents/{agentName}/logs")] = EndpointPolicy.Agent,
        [("PUT", "/api/agents/{name}/capabilities")] = EndpointPolicy.Agent,

        [("GET", "/api/packages/{digest}")] = EndpointPolicy.AgentOrViewer,

        [("GET", "/api/packages")] = EndpointPolicy.Viewer,
        [("GET", "/api/packages/{digest}/manifest")] = EndpointPolicy.Viewer,
        [("GET", "/api/agents")] = EndpointPolicy.Viewer,
        [("GET", "/api/agents/{name}")] = EndpointPolicy.Viewer,
        [("GET", "/api/agents/{agentName}/report/latest")] = EndpointPolicy.Viewer,
        [("GET", "/api/agents/{agentName}/logs")] = EndpointPolicy.Viewer,
        [("GET", "/api/application-policies")] = EndpointPolicy.Viewer,
        [("GET", "/api/endpoints")] = EndpointPolicy.Viewer,
        [("GET", "/api/endpoints/traefik")] = EndpointPolicy.Viewer,

        [("POST", "/api/packages")] = EndpointPolicy.Operator,
        [("DELETE", "/api/packages/{digest}")] = EndpointPolicy.Operator,
        [("PUT", "/api/agents/{name}/tags")] = EndpointPolicy.Operator,
        [("PUT", "/api/agents/{name}/scheduling")] = EndpointPolicy.Operator,
        [("DELETE", "/api/agents/{name}")] = EndpointPolicy.Operator,
        [("POST", "/api/agents/{name}/commands")] = EndpointPolicy.Operator,
        [("POST", "/api/application-policies")] = EndpointPolicy.Operator,
        [("PUT", "/api/application-policies/{id:guid}")] = EndpointPolicy.Operator,
        [("DELETE", "/api/application-policies/{id:guid}")] = EndpointPolicy.Operator,

        // Key and join-token management (AccessEndpoints): creating, listing and revoking are all
        // Operator - a Viewer key that could list keys would learn who holds the keys to the fleet.
        [("POST", "/api/api-keys")] = EndpointPolicy.Operator,
        [("GET", "/api/api-keys")] = EndpointPolicy.Operator,
        [("DELETE", "/api/api-keys/{name}")] = EndpointPolicy.Operator,
        [("POST", "/api/join-tokens")] = EndpointPolicy.Operator,
        [("GET", "/api/join-tokens")] = EndpointPolicy.Operator,
        [("DELETE", "/api/join-tokens/{id:guid}")] = EndpointPolicy.Operator,
        [("DELETE", "/api/agents/{name}/credential")] = EndpointPolicy.Operator,
    };

    /// <summary>Null means "not in the table" — refuse.</summary>
    public static EndpointPolicy? For(string method, string routePattern) =>
        Table.TryGetValue((method.ToUpperInvariant(), routePattern), out var policy) ? policy : null;

    /// <summary>The hub's own endpoints (negotiate and the connection) — any agent may connect; which group it may join is checked in the hub itself.</summary>
    public static bool IsHub(string routePattern) => routePattern.StartsWith(ApplicationPolicyHubContract.HubPath, StringComparison.Ordinal);
}
