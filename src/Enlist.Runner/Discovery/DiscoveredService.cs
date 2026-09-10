using System.Reflection;

namespace Enlist.Runner.Discovery;

public sealed record DiscoveredService(
    string Name,
    string? Description,
    Type Type,
    MethodInfo? StartMethod,
    MethodInfo? StopMethod)
{
    public string TypeName => Type.FullName ?? Type.Name;
}
