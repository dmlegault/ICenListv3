using System.Reflection;

namespace Enlist.Runner.Discovery;

public sealed record DiscoveryResult(
    IReadOnlyList<DiscoveredService> Services,
    IReadOnlyList<DiscoveredJob> Jobs,
    IReadOnlyList<string> Warnings,
    string? ApplicationDescription = null);

/// <summary>
/// Finds [EnlistService]/[EnlistJob] types by matching attribute NAMES, never by `is`/
/// IsAssignableFrom and never against a compiled Enlist.Attributes reference — the runner has no such
/// reference, on purpose (see contracts/EnlistAttributes.cs). Everything here reads
/// CustomAttributeData, which resolves without requiring the attribute's own assembly identity to
/// match anything on this side of the boundary.
/// </summary>
public static class PluginDiscovery
{
    private const string ServiceAttr = "EnlistServiceAttribute";
    private const string StartAttr = "EnlistStartAttribute";
    private const string StopAttr = "EnlistStopAttribute";
    private const string JobAttr = "EnlistJobAttribute";
    private const string ExecuteAttr = "EnlistExecuteAttribute";
    private const string ApplicationAttr = "EnlistApplicationAttribute";
    private const string ExpectedNamespace = "Enlist";

    public static DiscoveryResult Scan(IEnumerable<Assembly> assemblies)
    {
        var services = new List<DiscoveredService>();
        var jobs = new List<DiscoveredJob>();
        var warnings = new List<string>();

        // First one found wins — an application is expected to declare at most one, same as the design
        // doc's "one assembly-level attribute" framing; a plugin folder with multiple assemblies isn't
        // expected to have more than one carrying it.
        string? applicationDescription = null;

        foreach (var assembly in assemblies)
        {
            applicationDescription ??= FindApplicationDescription(assembly);

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // Partial failure is expected and not fatal — one unresolvable type (usually a
                // dependency the plugin doesn't actually need at runtime) must not hide every other
                // service/job in the same assembly. See enList v2's AppScanner for the same reasoning
                // applied to metadata-only scanning.
                types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
                warnings.Add($"{assembly.GetName().Name}: some types could not be loaded; continuing with the rest.");
            }

            foreach (var type in types)
            {
                if (!type.IsClass || type.IsAbstract)
                {
                    continue;
                }

                TryAddService(type, services, warnings);
                TryAddJob(type, jobs, warnings);
            }
        }

        return new DiscoveryResult(services, jobs, warnings, applicationDescription);
    }

    private static string? FindApplicationDescription(Assembly assembly)
    {
        foreach (var data in assembly.GetCustomAttributesData())
        {
            if (data.AttributeType.Name != ApplicationAttr || data.AttributeType.Namespace != ExpectedNamespace)
            {
                continue;
            }

            return ReadNamedArg(data, "Description");
        }

        return null;
    }

    private static void TryAddService(Type type, List<DiscoveredService> services, List<string> warnings)
    {
        var serviceAttr = FindAttribute(type, ServiceAttr, warnings);
        if (serviceAttr is null)
        {
            return;
        }

        var name = ReadStringArg(serviceAttr, position: 0) ?? type.Name;
        var description = ReadNamedArg(serviceAttr, "Description");

        var startMethod = FindMethod(type, StartAttr, warnings);
        var stopMethod = FindMethod(type, StopAttr, warnings);

        if (startMethod is null)
        {
            warnings.Add($"{type.FullName}: has [EnlistService] but no [EnlistStart] method - it will never be started.");
        }

        services.Add(new DiscoveredService(name, description, type, startMethod, stopMethod));
    }

    private static void TryAddJob(Type type, List<DiscoveredJob> jobs, List<string> warnings)
    {
        var jobAttr = FindAttribute(type, JobAttr, warnings);
        if (jobAttr is null)
        {
            return;
        }

        var name = ReadStringArg(jobAttr, position: 0) ?? type.Name;
        var description = ReadNamedArg(jobAttr, "Description");
        var cron = ReadNamedArg(jobAttr, "Cron");

        var executeMethod = FindMethod(type, ExecuteAttr, warnings);

        if (executeMethod is null)
        {
            warnings.Add($"{type.FullName}: has [EnlistJob] but no [EnlistExecute] method - it can never run.");
        }

        jobs.Add(new DiscoveredJob(name, description, cron, type, executeMethod));
    }

    private static CustomAttributeData? FindAttribute(MemberInfo member, string simpleName, List<string> warnings)
    {
        // Matched on the simple type name alone — this IS the "silent non-discovery" mitigation the
        // design doc calls for: a plugin whose attribute copy lives in the wrong namespace still gets
        // found (matching by name, never by type identity), and the namespace mismatch is reported
        // rather than swallowed, so a typo reads as a warning instead of an empty Services list.
        foreach (var data in member.GetCustomAttributesData())
        {
            if (data.AttributeType.Name != simpleName)
            {
                continue;
            }

            if (data.AttributeType.Namespace != ExpectedNamespace)
            {
                warnings.Add(
                    $"{DescribeMember(member)}: found {data.AttributeType.FullName}, expected " +
                    $"{ExpectedNamespace}.{simpleName}. Matched by name and used anyway, but this " +
                    "usually means a copy/paste picked up the wrong namespace.");
            }

            return data;
        }

        return null;
    }

    private static MethodInfo? FindMethod(Type type, string attributeSimpleName, List<string> warnings)
    {
        MethodInfo? found = null;

        // Inherited too: a base class carrying the lifecycle methods is an ordinary way to share them,
        // and an override appears once, as the most derived.
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (FindAttribute(method, attributeSimpleName, warnings) is null)
            {
                continue;
            }

            if (found is not null)
            {
                warnings.Add($"{type.FullName}: multiple methods carry [{attributeSimpleName[..^"Attribute".Length]}] - using {found.Name}, ignoring {method.Name}.");
                continue;
            }

            found = method;
        }

        return found;
    }

    private static string? ReadStringArg(CustomAttributeData data, int position)
    {
        if (position >= data.ConstructorArguments.Count)
        {
            return null;
        }

        return data.ConstructorArguments[position].Value as string;
    }

    private static string? ReadNamedArg(CustomAttributeData data, string name)
    {
        foreach (var named in data.NamedArguments)
        {
            if (named.MemberName == name)
            {
                return named.TypedValue.Value as string;
            }
        }

        return null;
    }

    private static string DescribeMember(MemberInfo member) =>
        member is Type t ? t.FullName ?? t.Name : $"{member.DeclaringType?.FullName}.{member.Name}";
}
