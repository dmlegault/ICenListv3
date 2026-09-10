// net472 predates C# 9's `init` accessor support, which contracts/EnlistAttributes.cs's Description
// properties use — the compiler needs this exact marker type present somewhere in the compilation.
// See src/Enlist.Runner.Legacy/IsExternalInit.cs for the fuller explanation; this project needs its
// own copy since EnlistAttributes.cs compiles directly into it as source, not a shared reference.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
