using System.Text.Json;
using System.Text.Json.Serialization;

namespace Enlist.Agent.Configuration;

public static class AssignmentStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        // Without this, System.Text.Json only accepts DesiredState as a raw integer (0/1), not the
        // string "Running"/"Stopped" any hand-written assignments.json would actually use.
        Converters = { new JsonStringEnumConverter() },
    };

    public static AgentAssignments Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Assignments file not found: {path}", path);
        }

        var json = File.ReadAllText(path);
        var assignments = JsonSerializer.Deserialize<AgentAssignments>(json, Options)
            ?? throw new InvalidOperationException($"{path} parsed to null.");

        foreach (var app in assignments.Applications)
        {
            if (!Directory.Exists(app.Path))
            {
                throw new DirectoryNotFoundException($"Application '{app.Name}' points at a directory that doesn't exist: {app.Path}");
            }
        }

        return assignments;
    }
}
