// Enlist plugin contract — distributed as SOURCE, never as a compiled assembly.
//
// Paste this file into your plugin project (or include it via the source-only package once one
// exists), do not add a ProjectReference/PackageReference to anything called "Enlist.Contracts".
// The runner discovers services and jobs by matching attribute type names as STRINGS, not by
// `is` / `IsAssignableFrom` — so your copy of these types never needs to be the same physical
// assembly as anyone else's. That is what lets a net472 plugin and a net10 plugin share one
// contract with zero shared binary.
//
// See docs/03-architecture/enList-v3-Design.md section 3 for the full rationale.

namespace Enlist;

/// <summary>Optional, assembly-level — describes the application as a whole, not any one service or job. At most one takes effect per application; put it anywhere in the project (e.g. AssemblyInfo.cs) as `[assembly: EnlistApplication(Description = "...")]`.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class EnlistApplicationAttribute : Attribute
{
    public string? Description { get; init; }
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class EnlistServiceAttribute : Attribute
{
    public string Name { get; }
    public string? Description { get; init; }

    public EnlistServiceAttribute(string name) => Name = name;
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class EnlistStartAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class EnlistStopAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class EnlistJobAttribute : Attribute
{
    public string Name { get; }
    public string? Description { get; init; }
    public string? Cron { get; init; }

    public EnlistJobAttribute(string name) => Name = name;
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class EnlistExecuteAttribute : Attribute
{
}

