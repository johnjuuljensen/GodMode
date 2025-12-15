using GodMode.Shared.Models;
using GodMode.Shared.Enums;

namespace GodMode.Maui.Abstractions;

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
    /// Lists all available project roots on the server
    /// </summary>
    Task<IEnumerable<ProjectRoot>> ListProjectRootsAsync();

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
    /// <param name="name">Project name. For worktree projects, this is also the branch name.</param>
    /// <param name="projectRootName">Name of the project root where the project will be created.</param>
    /// <param name="projectType">Type of project to create.</param>
    /// <param name="repoUrl">Repository URL (required for GitHubRepo and GitHubWorktree types).</param>
    /// <param name="initialPrompt">Initial prompt to send to Claude.</param>
    Task<ProjectDetail> CreateProjectAsync(
        string name,
        string projectRootName,
        ProjectType projectType,
        string? repoUrl,
        string initialPrompt);

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
    Task ResumeProjectAsync(string projectId);

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
    /// Disconnects from the host
    /// </summary>
    void Disconnect();
}
