using System.Text.Json;

namespace Enlist.Runner.Execution;

/// <summary>
/// Loads a plugin's settings from a colocated {TypeFullName}.settings.json (falling back to
/// {AssemblyName}.settings.json) next to its DLL — same "next to the assembly" convention enList v2
/// uses for IConfiguration, flattened into IDictionary&lt;string,string&gt; since that's the only
/// settings shape the zero-package contract can express (design doc section 3). Nested objects
/// flatten to colon-separated keys, matching .NET configuration convention.
/// </summary>
public static class SettingsLoader
{
    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    /// <param name="warn">Told about a settings file that exists but cannot be read — a typo in JSON used to be indistinguishable from having no settings at all.</param>
    public static IReadOnlyDictionary<string, string> Load(Type type, Action<string>? warn = null)
    {
        var location = type.Assembly.Location;
        if (string.IsNullOrEmpty(location))
        {
            return Empty;
        }

        var dir = Path.GetDirectoryName(location)!;
        var candidates = new[]
        {
            Path.Combine(dir, type.FullName + ".settings.json"),
            Path.Combine(dir, Path.GetFileNameWithoutExtension(location) + ".settings.json"),
        };

        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
        {
            return Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var result = new Dictionary<string, string>();
            Flatten(doc.RootElement, prefix: null, result);
            return result;
        }
        catch (Exception ex)
        {
            // Malformed settings must not crash a start/run — treated as "no settings", same as absent,
            // but said: the plugin runs without the values its author wrote down, and they should know.
            warn?.Invoke($"settings file '{Path.GetFileName(path)}' could not be read and was ignored - running with no file settings: {ex.Message}");
            return Empty;
        }
    }

    private static void Flatten(JsonElement element, string? prefix, Dictionary<string, string> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Flatten(property.Value, prefix is null ? property.Name : $"{prefix}:{property.Name}", into);
                }
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Flatten(item, prefix is null ? index.ToString() : $"{prefix}:{index}", into);
                    index++;
                }
                break;

            case JsonValueKind.String:
                if (prefix is not null)
                {
                    into[prefix] = element.GetString() ?? "";
                }
                break;

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                if (prefix is not null)
                {
                    into[prefix] = element.GetRawText();
                }
                break;

            case JsonValueKind.Null:
                if (prefix is not null)
                {
                    into[prefix] = "";
                }
                break;
        }
    }
}
