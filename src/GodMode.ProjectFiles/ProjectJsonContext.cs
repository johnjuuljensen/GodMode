using System.Text.Json;
using System.Text.Json.Serialization;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.ProjectFiles;

/// <summary>
/// JSON serialization context for project-related types using source generators.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(OutputEvent))]
[JsonSerializable(typeof(ProjectStatus))]
[JsonSerializable(typeof(ProjectSummary))]
[JsonSerializable(typeof(ProjectMetrics))]
[JsonSerializable(typeof(GitStatus))]
[JsonSerializable(typeof(TestStatus))]
[JsonSerializable(typeof(ProjectDetail))]
[JsonSerializable(typeof(OutputEventType))]
[JsonSerializable(typeof(ProjectState))]
[JsonSerializable(typeof(RootConfig))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
public partial class ProjectJsonContext : JsonSerializerContext
{
}
