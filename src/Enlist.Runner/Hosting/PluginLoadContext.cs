using System.Reflection;
using System.Runtime.Loader;

namespace Enlist.Runner.Hosting;

/// <summary>
/// A private assembly resolution scope for one application directory.
///
/// Unlike enList v2's PluginLoadContext (see enList_v2/src/AppInstance/Hosting/PluginLoadContext.cs),
/// the shared list here is genuinely EMPTY, not just small. There is no shared contract assembly to
/// special-case — Enlist attributes are matched by name (see Discovery/PluginDiscovery.cs), and the
/// only injectable parameters are CancellationToken, IDictionary&lt;string,string&gt; and
/// Action&lt;string&gt;/TextWriter, all of which live in the shared framework and therefore never
/// appear in a plugin's deps.json as a private asset — the resolver returns null for them on its own
/// and they fall through to the default context without needing an entry here.
///
/// That emptiness is not a nicety, it is what makes "zero PackageReferences in the runner" coherent:
/// the runner has nothing of its own for a plugin to collide with, and nothing it needs to keep in
/// sync with a plugin's version either.
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private const string DepsSuffix = ".deps.json";

    private readonly List<AssemblyDependencyResolver> _resolvers = new();
    private readonly List<string> _orphanedDeps = new();
    private readonly string _appPath;

    /// <summary>How many dependencies were resolved privately rather than falling through to the default context.</summary>
    public int PrivateResolutionCount { get; private set; }

    /// <summary>How many *.deps.json files yielded a usable resolver. Zero here means isolation never engaged.</summary>
    public int ResolverCount => _resolvers.Count;

    /// <summary>
    /// How many *.deps.json files were found at all, whether or not each yielded a resolver. THIS is
    /// the actual pass/fail signal for isolation, not ResolverCount — a plugin with no private
    /// dependencies legitimately has ResolverCount 0 while still isolated correctly. Zero here, for
    /// an application that should have one, means isolation never engaged for it at all.
    /// </summary>
    public int DepsFilesFound { get; private set; }

    public IReadOnlyList<string> OrphanedDeps => _orphanedDeps;

    public PluginLoadContext(string appPath)
        : base(name: $"Plugin:{Path.GetFileName(appPath.TrimEnd(Path.DirectorySeparatorChar))}", isCollectible: false)
    {
        _appPath = appPath;

        foreach (var depsPath in SafeEnumerate(appPath, "*.deps.json"))
        {
            DepsFilesFound++;

            var fileName = Path.GetFileName(depsPath);
            var assemblyName = fileName[..^DepsSuffix.Length];
            var dllPath = Path.Combine(appPath, assemblyName + ".dll");

            if (!File.Exists(dllPath))
            {
                _orphanedDeps.Add(fileName);
                continue;
            }

            try
            {
                _resolvers.Add(new AssemblyDependencyResolver(dllPath));
            }
            catch
            {
                // A malformed deps.json costs isolation for this one plugin, not the ability to run
                // it at all. The caller reports OrphanedDeps/ResolverCount as diagnostics.
                _orphanedDeps.Add(fileName);
            }
        }

        // Required so shared-framework code that resolves by name from the DEFAULT context (e.g.
        // ASP.NET Core's ApplicationPartManager) can still find a plugin assembly that only exists in
        // THIS context. See enList v2's PluginLoadContext.ResolveForDefaultContext for the full
        // explanation — same mechanism, carried over unchanged because the underlying CLR behavior is
        // unchanged.
        Default.Resolving += ResolveForDefaultContext;
    }

    private Assembly? ResolveForDefaultContext(AssemblyLoadContext _, AssemblyName assemblyName)
    {
        if (assemblyName.Name is null)
        {
            return null;
        }

        foreach (var loaded in Assemblies)
        {
            if (string.Equals(loaded.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
            {
                return loaded;
            }
        }

        foreach (var resolver in _resolvers)
        {
            var path = resolver.ResolveAssemblyToPath(assemblyName);
            if (path is not null && File.Exists(path))
            {
                PrivateResolutionCount++;
                return LoadFromAssemblyPath(path);
            }
        }

        var probed = Path.Combine(_appPath, assemblyName.Name + ".dll");
        if (File.Exists(probed))
        {
            PrivateResolutionCount++;
            return LoadFromAssemblyPath(probed);
        }

        return null;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is null)
        {
            return null;
        }

        foreach (var resolver in _resolvers)
        {
            var path = resolver.ResolveAssemblyToPath(assemblyName);
            if (path is not null && File.Exists(path))
            {
                PrivateResolutionCount++;
                return LoadFromAssemblyPath(path);
            }
        }

        var probed = Path.Combine(_appPath, assemblyName.Name + ".dll");
        if (File.Exists(probed))
        {
            PrivateResolutionCount++;
            return LoadFromAssemblyPath(probed);
        }

        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        foreach (var resolver in _resolvers)
        {
            var path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (path is not null)
            {
                return LoadUnmanagedDllFromPath(path);
            }
        }

        return IntPtr.Zero;
    }

    public Assembly LoadPlugin(string dllPath) => LoadFromAssemblyPath(dllPath);

    private static IEnumerable<string> SafeEnumerate(string path, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(path, pattern, SearchOption.TopDirectoryOnly).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
