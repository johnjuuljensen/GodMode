namespace GodMode.Voice.Tools;

public sealed class NewProjectTool : ITool
{
    public string Name => "new_project";
    public string Description => "Creates a new project with the given name and optional description.";

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter
        {
            Name = "name",
            Type = "string",
            Description = "The name for the new project.",
            Required = true
        },
        new ToolParameter
        {
            Name = "description",
            Type = "string",
            Description = "An optional description for the project.",
            Required = false
        }
    };

    private static readonly List<string> _createdProjects = new();

    public Task<ToolResult> ExecuteAsync(IDictionary<string, object> args)
    {
        var name = ExtractString(args, "name");
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(ToolResult.Fail(Name, "Missing required parameter: name"));

        var description = ExtractString(args, "description") ?? "No description provided.";

        if (_createdProjects.Contains(name, StringComparer.OrdinalIgnoreCase))
            return Task.FromResult(ToolResult.Fail(Name, $"Project '{name}' already exists."));

        _createdProjects.Add(name);

        var result = new
        {
            ProjectName = name,
            Description = description,
            CreatedAt = DateTime.UtcNow.ToString("o"),
            Status = "created"
        };

        var json = System.Text.Json.JsonSerializer.Serialize(result,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        return Task.FromResult(ToolResult.Ok(Name, json));
    }

    private static string? ExtractString(IDictionary<string, object> args, string key)
    {
        if (!args.TryGetValue(key, out var val))
            return null;
        if (val is string s) return s;
        if (val is System.Text.Json.JsonElement je) return je.GetString();
        return val?.ToString();
    }
}
