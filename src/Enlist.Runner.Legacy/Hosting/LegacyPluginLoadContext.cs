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
/// plain Assembly.LoadFrom, and there is no load context to separate them from what is already there.
///
/// That is NOT the same as "nothing is there". This comment claimed until 2026-09-13 that there was
/// "nothing else deliberately loaded here to collide with", and that was wrong: the runner's own
/// wire protocol needs System.Text.Json and System.Threading.Channels on net472 (see this project's
/// csproj, which carries the same correction), and both are loaded into this same AppDomain before
/// any plugin is. A net472 plugin shipping its own System.Text.Json at a different version collides
/// with the runner's copy, and no binding redirects are generated for this executable.
///
/// What DOES hold is the process boundary. Every application gets its own runner process, so the
/// collision surface is one plugin against those two assemblies - never one plugin against another.
/// The zero-PackageReference rule (contracts/EnlistAttributes.cs) is honored in the sense that
/// matters most, that the runner carries no dependency closure of its own beyond those two, but it is
/// honored less completely here than on net10.0, where the modern runner needs neither package and
/// AssemblyLoadContext separates what remains.
///
/// PluginDiscovery's own IsolationInfo is reported honestly as "did not engage" for every application
/// this runner hosts, rather than fabricating metrics that don't apply.
/// </summary>
public sealed class LegacyPluginLoadContext
{
    /// <summary>Takes the application path for symmetry with the modern PluginLoadContext, which probes it. This one does not: LoadFrom resolves from the assembly's own directory, so there is nothing to probe.</summary>
    public LegacyPluginLoadContext(string appPath) { }

    /// <summary>
    /// Assembly.LoadFrom (not LoadFile) deliberately — LoadFrom participates in the CLR's normal probing
    /// so a plugin's own co-located dependency DLLs are found automatically, the same practical benefit
    /// PluginLoadContext's AssemblyDependencyResolver gives the modern runner, just via the runtime's
    /// own default mechanism rather than an explicit resolver.
    /// </summary>
    public Assembly LoadPlugin(string dllPath) => Assembly.LoadFrom(dllPath);
}
