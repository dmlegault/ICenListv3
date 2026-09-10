using System.Reflection;
using System.Runtime.InteropServices;

using Enlist.ControlPlane.Contracts;

namespace Enlist.ControlPlane;

/// <summary>
/// Reflection-ONLY scan (MetadataLoadContext — nothing in the uploaded package is ever executed,
/// static constructors never run) for [EnlistService]/[EnlistJob]-attributed types, extracting just
/// Name/Description for the portal's "what's in this package" dialog. Matches by the attribute's
/// SIMPLE type name, mirroring Enlist.Runner.Discovery.PluginDiscovery's own reasoning
/// (contracts/EnlistAttributes.cs is distributed as source, pasted into each plugin project — never a
/// compiled reference either side can depend on). Duplicated rather than shared with the runner
/// project since the control plane has no other reason to reference it, and the runner's own version
/// loads assemblies via a real, executable AssemblyLoadContext — appropriate there (it's about to run
/// the plugin), wrong here (an uploaded package the control plane itself never runs).
/// </summary>
public static class PackageManifestScanner
{
    private const string ServiceAttr = "EnlistServiceAttribute";
    private const string JobAttr = "EnlistJobAttribute";
    private const string ApplicationAttr = "EnlistApplicationAttribute";

    public static PackageManifestDto Scan(string extractedDirectory)
    {
        var dlls = Directory.GetFiles(extractedDirectory, "*.dll", SearchOption.AllDirectories);
        if (dlls.Length == 0)
        {
            return new PackageManifestDto(null, []);
        }

        MetadataLoadContext context;
        try
        {
            var runtimeAssemblies = Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll");
            context = new MetadataLoadContext(new PathAssemblyResolver(runtimeAssemblies.Concat(dlls).Distinct()));
        }
        catch (Exception ex)
        {
            // A DLL whose simple name collides with a runtime assembly makes the resolver itself refuse.
            // Named as what it is, so the upload logs "no manifest for this package" rather than a bare
            // constructor exception.
            throw new InvalidOperationException($"The package's assemblies could not be prepared for a metadata scan: {ex.Message}", ex);
        }

        using var _ = context;

        var entries = new List<PackageManifestEntryDto>();
        string? applicationDescription = null;

        foreach (var dllPath in dlls)
        {
            Assembly assembly;
            try
            {
                assembly = context.LoadFromAssemblyPath(dllPath);
            }
            catch
            {
                // Not a managed assembly, or one whose dependencies can't resolve in this shallow
                // context — skip it, same "partial failure isn't fatal" posture PluginDiscovery uses.
                continue;
            }

            applicationDescription ??= FindApplicationDescription(assembly);

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray()!;
            }

            foreach (var type in types)
            {
                if (!type.IsClass || type.IsAbstract)
                {
                    continue;
                }

                AddIfMatches(type, ServiceAttr, "Service", entries);
                AddIfMatches(type, JobAttr, "Job", entries);
            }
        }

        return new PackageManifestDto(applicationDescription, entries);
    }

    private static string? FindApplicationDescription(Assembly assembly)
    {
        foreach (var data in assembly.GetCustomAttributesData())
        {
            if (data.AttributeType.Name != ApplicationAttr)
            {
                continue;
            }

            return ReadNamedArg(data, "Description");
        }

        return null;
    }

    private static void AddIfMatches(Type type, string attributeSimpleName, string kind, List<PackageManifestEntryDto> entries)
    {
        foreach (var data in type.GetCustomAttributesData())
        {
            if (data.AttributeType.Name != attributeSimpleName)
            {
                continue;
            }

            var name = data.ConstructorArguments.Count > 0 ? data.ConstructorArguments[0].Value as string : null;
            var description = ReadNamedArg(data, "Description");
            entries.Add(new PackageManifestEntryDto(name ?? type.Name, kind, description));
            return;
        }
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
}
