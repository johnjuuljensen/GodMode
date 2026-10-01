using System.Text.Json;

namespace GodMode.Shared.Models;

/// <summary>
/// Request to create a new project.
/// </summary>
/// <param name="ProfileName">The name of the profile the root belongs to.</param>
/// <param name="ProjectRootName">The name of the project root where the project will be created.</param>
/// <param name="Inputs">Form inputs as key-value pairs from the dynamic form.</param>
/// <param name="ActionName">The name of the create action to use. Null uses the first/default action.</param>
/// <param name="ParentId">
/// The ID of the session starting this one, its <see cref="ProjectStatus.ParentId"/>: a session this
/// server tracks, else the create is refused. Null for a top-level session.
/// </param>
public record CreateProjectRequest(
    string ProfileName,
    string ProjectRootName,
    Dictionary<string, JsonElement> Inputs,
    string? ActionName = null,
    string? ParentId = null
)
{
    /// <summary>
    /// The input that carries <see cref="ParentId"/> through <see cref="Hubs.IProjectHub.CreateProject"/>,
    /// whose four parameters every caller passes (a hub method's arguments are all required).
    /// </summary>
    public const string ParentInput = "__parentId";
}
