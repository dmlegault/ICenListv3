using System.Reflection;

namespace Enlist.Runner.Legacy.Discovery;

public sealed record DiscoveredService(
    string Name,
    string? Description,
    Type Type,
    MethodInfo? StartMethod,
    MethodInfo? StopMethod)
{
    public string TypeName => Type.FullName ?? Type.Name;
}
