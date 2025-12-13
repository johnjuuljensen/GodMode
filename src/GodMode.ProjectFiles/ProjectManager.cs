using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.ProjectFiles;

/// <summary>
/// Manager for discovering and managing multiple project folders.
/// </summary>
public sealed class ProjectManager
{
    private readonly string _rootPath;

    /// <summary>
    /// Gets the root path where project folders are stored.
    /// </summary>
    public string RootPath => _rootPath;

    /// <summary>
    /// Creates a new ProjectManager for the specified root path.
    /// </summary>
    /// <param name="rootPath">Root directory where project folders are stored.</param>
    /// <exception cref="ArgumentException">Thrown when rootPath is invalid.</exception>
    public ProjectManager(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Root path cannot be empty.", nameof(rootPath));

        _rootPath = Path.GetFullPath(rootPath);

        // Ensure root directory exists
        if (!Directory.Exists(_rootPath))
            Directory.CreateDirectory(_rootPath);
    }

    /// <summary>
    /// Lists all project folders in the root directory.
    /// </summary>
    /// <returns>Array of project folder paths.</returns>
    public string[] ListProjectPaths()
    {
        if (!Directory.Exists(_rootPath))
            return Array.Empty<string>();

        return Directory.GetDirectories(_rootPath)
            .Where(IsValidProjectFolder)
            .ToArray();
    }

    /// <summary>
    /// Lists all project summaries in the root directory.
    /// </summary>
    /// <returns>Array of project summaries.</returns>
    public ProjectSummary[] ListProjects()
    {
        var projectPaths = ListProjectPaths();
        var summaries = new List<ProjectSummary>();

        foreach (var path in projectPaths)
        {
            try
            {
                using var project = ProjectFolder.Open(path);
                var status = project.ReadStatus();
                summaries.Add(new ProjectSummary(
                    status.Id,
                    status.Name,
                    status.State,
                    status.UpdatedAt,
                    status.CurrentQuestion
                ));
            }
            catch
            {
                // Skip invalid projects
                continue;
            }
        }

        return summaries.OrderByDescending(p => p.UpdatedAt).ToArray();
    }

    /// <summary>
    /// Lists all project summaries asynchronously.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Array of project summaries.</returns>
    public async Task<ProjectSummary[]> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        var projectPaths = ListProjectPaths();
        var summaries = new List<ProjectSummary>();

        foreach (var path in projectPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var project = ProjectFolder.Open(path);
                var status = await project.ReadStatusAsync(cancellationToken);
                summaries.Add(new ProjectSummary(
                    status.Id,
                    status.Name,
                    status.State,
                    status.UpdatedAt,
                    status.CurrentQuestion
                ));
            }
            catch
            {
                // Skip invalid projects
                continue;
            }
        }

        return summaries.OrderByDescending(p => p.UpdatedAt).ToArray();
    }

    /// <summary>
    /// Creates a new project.
    /// </summary>
    /// <param name="projectId">Unique project identifier.</param>
    /// <param name="name">Human-readable project name.</param>
    /// <param name="repoUrl">Optional repository URL.</param>
    /// <returns>A new ProjectFolder instance.</returns>
    public ProjectFolder CreateProject(string projectId, string name, string? repoUrl = null)
    {
        return ProjectFolder.Create(_rootPath, projectId, name, repoUrl);
    }

    /// <summary>
    /// Opens an existing project by ID.
    /// </summary>
    /// <param name="projectId">The project ID.</param>
    /// <returns>A ProjectFolder instance.</returns>
    /// <exception cref="DirectoryNotFoundException">Thrown when project doesn't exist.</exception>
    public ProjectFolder OpenProject(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("Project ID cannot be empty.", nameof(projectId));

        var projectPath = Path.Combine(_rootPath, projectId);
        return ProjectFolder.Open(projectPath);
    }

    /// <summary>
    /// Checks if a project exists.
    /// </summary>
    /// <param name="projectId">The project ID.</param>
    /// <returns>True if project exists and is valid.</returns>
    public bool ProjectExists(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            return false;

        var projectPath = Path.Combine(_rootPath, projectId);
        return IsValidProjectFolder(projectPath);
    }

    /// <summary>
    /// Deletes a project folder and all its contents.
    /// </summary>
    /// <param name="projectId">The project ID.</param>
    /// <param name="force">If true, deletes even if project is in running state.</param>
    /// <exception cref="InvalidOperationException">Thrown when trying to delete a running project without force.</exception>
    public void DeleteProject(string projectId, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("Project ID cannot be empty.", nameof(projectId));

        var projectPath = Path.Combine(_rootPath, projectId);

        if (!Directory.Exists(projectPath))
            return;

        if (!force)
        {
            // Check if project is running
            try
            {
                using var project = ProjectFolder.Open(projectPath);
                var status = project.ReadStatus();
                if (status.State == ProjectState.Running)
                {
                    throw new InvalidOperationException(
                        $"Cannot delete running project '{projectId}'. Stop the project first or use force=true.");
                }
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                // If we can't read status, allow deletion
            }
        }

        Directory.Delete(projectPath, recursive: true);
    }

    /// <summary>
    /// Deletes a project folder asynchronously.
    /// </summary>
    /// <param name="projectId">The project ID.</param>
    /// <param name="force">If true, deletes even if project is in running state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task DeleteProjectAsync(string projectId, bool force = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("Project ID cannot be empty.", nameof(projectId));

        var projectPath = Path.Combine(_rootPath, projectId);

        if (!Directory.Exists(projectPath))
            return;

        if (!force)
        {
            // Check if project is running
            try
            {
                using var project = ProjectFolder.Open(projectPath);
                var status = await project.ReadStatusAsync(cancellationToken);
                if (status.State == ProjectState.Running)
                {
                    throw new InvalidOperationException(
                        $"Cannot delete running project '{projectId}'. Stop the project first or use force=true.");
                }
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                // If we can't read status, allow deletion
            }
        }

        await Task.Run(() => Directory.Delete(projectPath, recursive: true), cancellationToken);
    }

    /// <summary>
    /// Gets the full path for a project ID.
    /// </summary>
    /// <param name="projectId">The project ID.</param>
    /// <returns>Full path to the project folder.</returns>
    public string GetProjectPath(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("Project ID cannot be empty.", nameof(projectId));

        return Path.Combine(_rootPath, projectId);
    }

    private static bool IsValidProjectFolder(string path)
    {
        if (!Directory.Exists(path))
            return false;

        // Check for required files
        var statusFile = Path.Combine(path, "status.json");
        return File.Exists(statusFile);
    }
}
