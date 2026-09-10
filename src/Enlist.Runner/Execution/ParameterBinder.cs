using System.Reflection;

using Enlist.Runner.Logging;

namespace Enlist.Runner.Execution;

/// <summary>
/// Binds [EnlistStart]/[EnlistStop]/[EnlistExecute]/... method parameters by TYPE, from the fixed
/// BCL-only pool described in docs/03-architecture/enList-v3-Design.md section 3. Deliberately a closed set — adding
/// ILogger&lt;T&gt; or IConfiguration here is exactly the change that would put an Abstractions
/// package back into the runner's dependency graph.
/// </summary>
public static class ParameterBinder
{
    public static object?[] Bind(MethodInfo method, CancellationToken token, IReadOnlyDictionary<string, string> settings, Action<string> log)
    {
        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var type = parameters[i].ParameterType;

            if (type == typeof(CancellationToken))
            {
                args[i] = token;
            }
            else if (type == typeof(IDictionary<string, string>) || type == typeof(Dictionary<string, string>))
            {
                args[i] = new Dictionary<string, string>(settings);
            }
            else if (type == typeof(IReadOnlyDictionary<string, string>))
            {
                args[i] = settings;
            }
            else if (type == typeof(Action<string>))
            {
                args[i] = log;
            }
            else if (type == typeof(TextWriter))
            {
                args[i] = new LineBufferingTextWriter(log);
            }
            else
            {
                throw new NotSupportedException(
                    $"Parameter '{parameters[i].Name}' of {method.DeclaringType?.FullName}.{method.Name} has type " +
                    $"{type}, which is not one of the injectable types the runner supports: CancellationToken, " +
                    "IDictionary<string,string>, IReadOnlyDictionary<string,string>, Action<string>, TextWriter. " +
                    "See docs/03-architecture/enList-v3-Design.md section 3.");
            }
        }

        return args;
    }
}
