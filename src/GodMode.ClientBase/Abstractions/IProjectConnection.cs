using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.ClientBase.Abstractions;

/// <summary>
/// Represents a connection to a host for managing projects
/// </summary>
public interface IProjectConnection : IDisposable
{
    /// <summary>
    /// Gets whether the connection is currently active
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Lists all available project roots with their input schemas
    /// </summary>
    Task<IEnumerable<ProjectRootInfo>> ListProjectRootsAsync();

    /// <summary>
    /// Lists all projects on the connected host
    /// </summary>
    Task<IEnumerable<ProjectSummary>> ListProjectsAsync();

    /// <summary>
    /// Gets the current status of a specific project
    /// </summary>
    Task<ProjectStatus> GetStatusAsync(string projectId);

    /// <summary>
    /// Creates a new project with the specified parameters
    /// </summary>
    /// <param name="projectRootName">Name of the project root (null for workspace-less projects).</param>
    /// <param name="inputs">Form inputs as key-value pairs from the dynamic form.</param>
    /// <param name="environment">Optional client-injected environment variables.</param>
    Task<ProjectDetail> CreateProjectAsync(string? projectRootName, Dictionary<string, JsonElement> inputs, Dictionary<string, string>? environment = null);

    /// <summary>
    /// Sends user input to a project waiting for input
    /// </summary>
    Task SendInputAsync(string projectId, string input);

    /// <summary>
    /// Stops a running project
    /// </summary>
    Task StopProjectAsync(string projectId);

    /// <summary>
    /// Resumes a stopped project using its existing session
    /// </summary>
    /// <param name="environment">Optional client-injected environment variables.</param>
    Task ResumeProjectAsync(string projectId, Dictionary<string, string>? environment = null);

    /// <summary>
    /// Subscribes to output messages from a project, starting from a specific offset.
    /// Returns raw Claude JSON messages wrapped in ClaudeMessage for UI rendering.
    /// </summary>
    IObservable<ClaudeMessage> SubscribeOutput(string projectId, long fromOffset = 0);

    /// <summary>
    /// Gets the metrics HTML for a project
    /// </summary>
    Task<string> GetMetricsHtmlAsync(string projectId);

    /// <summary>
    /// Deletes a project on the server
    /// </summary>
    Task DeleteProjectAsync(string projectId);

    /// <summary>
    /// Lists known repositories configured on the server
    /// </summary>
    Task<IEnumerable<RepoInfo>> ListKnownReposAsync();

    /// <summary>
    /// Event raised when the server streams creation progress for a project.
    /// Parameters: projectId, message.
    /// </summary>
    event Action<string, string>? CreationProgressReceived;

    /// <summary>
    /// Event raised when a new project is created (broadcast from server).
    /// </summary>
    event Action<ProjectStatus>? ProjectCreatedReceived;

    /// <summary>
    /// Event raised when any project's status changes (broadcast from server).
    /// Parameters: projectId, new status.
    /// </summary>
    event Action<string, ProjectStatus>? StatusChangedReceived;

    /// <summary>
    /// Event raised when a project is deleted (broadcast from server).
    /// Parameter: projectId.
    /// </summary>
    event Action<string>? ProjectDeletedReceived;

    /// <summary>
    /// Disconnects from the host
    /// </summary>
    void Disconnect();
}
