// net472 predates the BCL types several modern C# language features rely on being present somewhere
// in the compilation — netcoreapp3.0+/net7.0+ ship these themselves; net472 does not, so they're
// polyfilled here. None of these are ever referenced directly by any code in this project; their mere
// presence is what the compiler needs.
namespace System.Runtime.CompilerServices
{
    // C# 9 `init`-only accessors (every record's positional properties, including every
    // RunnerMessage/AgentCommand type ported from Enlist.Runner, are `init`-only).
    internal static class IsExternalInit
    {
    }

    // C# 11 `required` members (RunningServiceState.Service/Instance/StoppingCts).
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) => FeatureName = featureName;
        public string FeatureName { get; }
        public bool IsOptional { get; init; }
    }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute
    {
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Constructor, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute
    {
    }
}
