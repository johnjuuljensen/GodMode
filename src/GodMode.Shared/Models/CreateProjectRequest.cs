using System.Text.Json;

namespace GodMode.Shared.Models;

/// <summary>
/// Request to create a new project.
/// </summary>
/// <param name="ProjectRootName">The name of the project root (null for workspace-less projects).</param>
/// <param name="Inputs">Form inputs as key-value pairs from the dynamic form.</param>
/// <param name="Environment">Optional client-injected environment variables (e.g. resolved credentials).</param>
public record CreateProjectRequest(
    string? ProjectRootName,
    Dictionary<string, JsonElement> Inputs,
    Dictionary<string, string>? Environment = null
);
