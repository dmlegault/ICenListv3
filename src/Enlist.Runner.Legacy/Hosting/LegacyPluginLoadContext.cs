using System.Reflection;

namespace Enlist.Runner.Legacy.Hosting;

/// <summary>
/// The net472 stand-in for the modern Enlist.Runner's PluginLoadContext — and the one piece of this
/// port that is NOT a faithful translation, because there is nothing to translate it into.
/// AssemblyLoadContext (System.Runtime.Loader) is a .NET Core+ concept with no net472 equivalent; the
/// closest true isolation primitive .NET Framework offers is a separate AppDomain, which this first
/// cut deliberately does NOT use.
///
/// Why not: real AppDomain isolation means the runner's discovery/invocation code would need to run
/// INSIDE the child domain (a plugin's Type/MethodInfo objects cannot cross an AppDomain boundary by
/// reference), which means either marshaling every call through a MarshalByRefObject proxy interface,
/// or running a second copy of RunnerHost's own logic inside the child domain and remoting only
/// coarse-grained results back — real, non-trivial complexity with its own well-known failure modes
/// (serialization boundary errors, remoting lifetime/lease issues) that would need to be gotten right
/// before this runner could be trusted with anything. See docs/03-architecture/SAD.md §9 and docs/05-operations/Runbook.md.
///
/// What this means in practice: plugin assemblies load directly into THIS process's one AppDomain via
/// plain Assembly.LoadFrom. The zero-PackageReference rule (contracts/EnlistAttributes.cs) is still
/// fully honored — that rule is about this project never carrying a dependency closure a plugin could
/// collide against, and Assembly.LoadFrom needs no package to do that. What's NOT provided is
/// isolation BETWEEN two plugins hosted by two different instances of this runner (each is still its
/// own OS process, so that boundary holds) — the gap is narrower than it sounds, and specifically
/// affects only a single legacy plugin whose own private dependencies collide with something already
/// loaded into this process (there is nothing else deliberately loaded here to collide with).
/// PluginDiscovery's own IsolationInfo is reported honestly as "did not engage" for every application
/// this runner hosts, rather than fabricating metrics that don't apply.
/// </summary>
public sealed class LegacyPluginLoadContext
{
    private readonly string _appPath;

    public LegacyPluginLoadContext(string appPath) => _appPath = appPath;

    /// <summary>
    /// Assembly.LoadFrom (not LoadFile) deliberately — LoadFrom participates in the CLR's normal probing
    /// so a plugin's own co-located dependency DLLs are found automatically, the same practical benefit
    /// PluginLoadContext's AssemblyDependencyResolver gives the modern runner, just via the runtime's
    /// own default mechanism rather than an explicit resolver.
    /// </summary>
    public Assembly LoadPlugin(string dllPath) => Assembly.LoadFrom(dllPath);
}
