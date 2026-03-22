using System.Text.Json.Serialization;

namespace GodMode.Shared.Enums;

/// <summary>
/// Represents the current state of a project.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProjectState>))]
public enum ProjectState
{
    /// <summary>
    /// Project is idle and not executing.
    /// </summary>
    Idle,

    /// <summary>
    /// Project is currently running.
    /// </summary>
    Running,

    /// <summary>
    /// Project is waiting for user input.
    /// </summary>
    WaitingInput,

    /// <summary>
    /// Project encountered an error.
    /// </summary>
    Error,

    /// <summary>
    /// Project has been stopped.
    /// </summary>
    Stopped
}
