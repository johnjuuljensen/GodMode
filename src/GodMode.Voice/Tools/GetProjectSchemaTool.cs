namespace GodMode.Voice.Tools;

public sealed class GetProjectSchemaTool : ITool
{
    public string Name => "get_project_schema";
    public string Description => "Retrieves the schema definition for a specified project, including its entities, fields, and relationships.";

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter
        {
            Name = "project_name",
            Type = "string",
            Description = "The name of the project whose schema to retrieve.",
            Required = true
        }
    };

    // Demo project schemas
    private static readonly Dictionary<string, object> _schemas = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MyProject"] = new
        {
            Name = "MyProject",
            Entities = new[]
            {
                new { Name = "User", Fields = new[] { "Id (int)", "Name (string)", "Email (string)" } },
                new { Name = "Task", Fields = new[] { "Id (int)", "Title (string)", "AssignedTo (User)", "Status (string)" } }
            }
        },
        ["TestProject"] = new
        {
            Name = "TestProject",
            Entities = new[]
            {
                new { Name = "TestCase", Fields = new[] { "Id (int)", "Name (string)", "Expected (string)", "Actual (string)" } }
            }
        }
    };

    public Task<ToolResult> ExecuteAsync(IDictionary<string, object> args)
    {
        var projectName = ExtractString(args, "project_name");
        if (string.IsNullOrWhiteSpace(projectName))
            return Task.FromResult(ToolResult.Fail(Name, "Missing required parameter: project_name"));

        if (!_schemas.TryGetValue(projectName, out var schema))
        {
            return Task.FromResult(ToolResult.Fail(Name,
                $"Project '{projectName}' not found. Available projects: {string.Join(", ", _schemas.Keys)}"));
        }

        var json = System.Text.Json.JsonSerializer.Serialize(schema,
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
