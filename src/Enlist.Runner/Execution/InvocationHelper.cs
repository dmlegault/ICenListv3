using System.Reflection;

namespace Enlist.Runner.Execution;

/// <summary>
/// Invokes a plugin method and awaits it if it returned something awaitable. Supports void and
/// Task/Task&lt;T&gt; only — ValueTask/ValueTask&lt;T&gt; are rejected explicitly rather than
/// silently not-awaited, since a fire-and-forget ValueTask is a correctness bug (the underlying
/// operation may still be running, or may only be safe to consume once) that must not pass as
/// success.
/// </summary>
public static class InvocationHelper
{
    public static async Task InvokeAsync(MethodInfo method, object instance, object?[] args)
    {
        var result = method.Invoke(instance, args);

        if (result is Task task)
        {
            await task.ConfigureAwait(false);
            return;
        }

        if (result is not null && IsUnsupportedAwaitable(result.GetType()))
        {
            throw new NotSupportedException(
                $"{method.DeclaringType?.FullName}.{method.Name} returns {result.GetType()}, which the runner does " +
                "not await. Supported return types are void and Task/Task<T>.");
        }
    }

    private static bool IsUnsupportedAwaitable(Type type) =>
        type == typeof(ValueTask) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>));
}
