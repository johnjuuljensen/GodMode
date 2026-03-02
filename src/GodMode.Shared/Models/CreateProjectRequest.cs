using System.Text.Json;

namespace GodMode.Shared.Models;

/// <summary>
/// Request to create a new project.
/// </summary>
/// <param name="ProfileName">The name of the profile the root belongs to.</param>
/// <param name="ProjectRootName">The name of the project root where the project will be created.</param>
/// <param name="Inputs">Form inputs as key-value pairs from the dynamic form.</param>
/// <param name="ActionName">The name of the create action to use. Null uses the first/default action.</param>
public record CreateProjectRequest(
    string ProfileName,
    string ProjectRootName,
    Dictionary<string, JsonElement> Inputs,
    string? ActionName = null
);
