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
    Task<ProjectDetail> CreateProjectAsync(string name, string? repoUrl, string initialPrompt);

    /// <summary>
    /// Sends user input to a project waiting for input
    /// </summary>
    Task SendInputAsync(string projectId, string input);

    /// <summary>
    /// Stops a running project
    /// </summary>
    Task StopProjectAsync(string projectId);

    /// <summary>
    /// Subscribes to output events from a project, starting from a specific offset
    /// </summary>
    IObservable<OutputEvent> SubscribeOutput(string projectId, long fromOffset = 0);

    /// <summary>
    /// Gets the metrics HTML for a project
    /// </summary>
    Task<string> GetMetricsHtmlAsync(string projectId);

    /// <summary>
    /// Disconnects from the host
    /// </summary>
    void Disconnect();
}
