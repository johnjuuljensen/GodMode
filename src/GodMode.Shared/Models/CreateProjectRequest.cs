using System.Text.Json;

namespace GodMode.Shared.Models;

/// <summary>
/// Request to create a new project.
/// </summary>
/// <param name="ProjectRootName">The name of the project root where the project will be created.</param>
/// <param name="Inputs">Form inputs as key-value pairs from the dynamic form.</param>
public record CreateProjectRequest(
    string ProjectRootName,
    Dictionary<string, JsonElement> Inputs
);
