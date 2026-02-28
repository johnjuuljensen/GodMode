using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.ProjectFiles;

/// <summary>
/// Manager for discovering and managing multiple project folders across named project roots.
/// VCS-agnostic — all creation logic lives in scripts, not here.
/// </summary>
public sealed class ProjectManager
{
    private readonly Dictionary<string, string> _projectRoots;

    /// <summary>
    /// Gets the named project roots.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProjectRoots => _projectRoots;

    /// <summary>
    /// Creates a new ProjectManager with the specified named project roots.
    /// </summary>
    /// <param name="projectRoots">Dictionary of named project roots (name -> path).</param>
    /// <exception cref="ArgumentException">Thrown when projectRoots is null or empty.</exception>
    public ProjectManager(IReadOnlyDictionary<string, string> projectRoots)
    {
        if (projectRoots == null || projectRoots.Count == 0)
            throw new ArgumentException("At least one project root must be specified.", nameof(projectRoots));

        _projectRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, path) in projectRoots)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Project root name cannot be empty.", nameof(projectRoots));

            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException($"Project root path for '{name}' cannot be empty.", nameof(projectRoots));

            var fullPath = Path.GetFullPath(path);

            // Ensure root directory exists
            if (!Directory.Exists(fullPath))
                Directory.CreateDirectory(fullPath);

            _projectRoots[name] = fullPath;
        }
    }

    /// <summary>
    /// Creates a new ProjectManager for a single root path (backward compatibility).
    /// </summary>
    /// <param name="rootPath">Root directory where project folders are stored.</param>
    /// <exception cref="ArgumentException">Thrown when rootPath is invalid.</exception>
    public ProjectManager(string rootPath)
        : this(new Dictionary<string, string> { ["default"] = rootPath })
    {
    }

    /// <summary>
    /// Gets the path for a named project root.
    /// </summary>
    /// <param name="rootName">The name of the project root.</param>
    /// <returns>The full path to the project root.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when root name is not found.</exception>
    public string GetProjectRootPath(string rootName)
    {
        if (!_projectRoots.TryGetValue(rootName, out var path))
            throw new KeyNotFoundException($"Project root '{rootName}' not found.");

        return path;
    }

    /// <summary>
    /// Lists all project folders across all project roots.
    /// </summary>
    /// <returns>Array of project folder paths.</returns>
    public string[] ListProjectPaths()
    {
        var paths = new List<string>();

        foreach (var rootPath in _projectRoots.Values)
        {
            if (!Directory.Exists(rootPath))
                continue;

            paths.AddRange(
                Directory.GetDirectories(rootPath)
                    .Where(IsValidProjectFolder)
            );
        }

        return paths.ToArray();
    }

    /// <summary>
    /// Lists all project folders in a specific project root.
    /// </summary>
    /// <param name="rootName">The name of the project root.</param>
    /// <returns>Array of project folder paths.</returns>
    public string[] ListProjectPaths(string rootName)
    {
        var rootPath = GetProjectRootPath(rootName);

        if (!Directory.Exists(rootPath))
            return Array.Empty<string>();

        return Directory.GetDirectories(rootPath)
            .Where(IsValidProjectFolder)
            .ToArray();
    }

    /// <summary>
    /// Lists all project summaries across all project roots.
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
    /// Creates a new project in the specified project root.
    /// VCS-agnostic — just creates the folder with .godmode state.
    /// </summary>
    /// <param name="rootName">The name of the project root.</param>
    /// <param name="name">Human-readable project name.</param>
    /// <returns>A new ProjectFolder instance and the project ID.</returns>
    /// <exception cref="ArgumentException">Thrown when required parameters are missing.</exception>
    public (ProjectFolder Folder, string ProjectId) CreateProject(string rootName, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Project name cannot be empty.", nameof(name));

        var rootPath = GetProjectRootPath(rootName);
        var projectId = ConvertNameToPath(name);
        var folder = ProjectFolder.Create(rootPath, projectId, name);
        return (folder, projectId);
    }

    /// <summary>
    /// Converts a display name to a path-safe project ID.
    /// Spaces become underscores; invalid filename characters are removed.
    /// </summary>
    public static string ConvertNameToPath(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => c == ' ' ? '_' : c)
            .Where(c => !invalidChars.Contains(c))
            .ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "project" : cleaned;
    }

    /// <summary>
    /// Converts a path-safe project ID back to a display name.
    /// Convention: underscores become spaces.
    /// </summary>
    public static string ConvertPathToName(string path)
    {
        return path.Replace('_', ' ');
    }

    /// <summary>
    /// Opens an existing project by ID, searching across all project roots.
    /// </summary>
    /// <param name="projectId">The project ID.</param>
    /// <returns>A ProjectFolder instance.</returns>
    /// <exception cref="DirectoryNotFoundException">Thrown when project doesn't exist.</exception>
    public ProjectFolder OpenProject(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("Project ID cannot be empty.", nameof(projectId));

        var projectPath = FindProjectPath(projectId);
        if (projectPath == null)
            throw new DirectoryNotFoundException($"Project '{projectId}' not found in any project root.");

        return ProjectFolder.Open(projectPath);
    }

    /// <summary>
    /// Opens an existing project by ID in a specific project root.
    /// </summary>
    /// <param name="rootName">The name of the project root.</param>
    /// <param name="projectId">The project ID.</param>
    /// <returns>A ProjectFolder instance.</returns>
    /// <exception cref="DirectoryNotFoundException">Thrown when project doesn't exist.</exception>
    public ProjectFolder OpenProject(string rootName, string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("Project ID cannot be empty.", nameof(projectId));

        var rootPath = GetProjectRootPath(rootName);
        var projectPath = Path.Combine(rootPath, projectId);
        return ProjectFolder.Open(projectPath);
    }

    /// <summary>
    /// Checks if a project exists in any project root.
    /// </summary>
    /// <param name="projectId">The project ID.</param>
    /// <returns>True if project exists and is valid.</returns>
    public bool ProjectExists(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            return false;

        return FindProjectPath(projectId) != null;
    }

    /// <summary>
    /// Finds the full path to a project by searching all project roots.
    /// </summary>
    /// <param name="projectId">The project ID.</param>
    /// <returns>The full path to the project, or null if not found.</returns>
    public string? FindProjectPath(string projectId)
    {
        foreach (var rootPath in _projectRoots.Values)
        {
            var projectPath = Path.Combine(rootPath, projectId);
            if (IsValidProjectFolder(projectPath))
                return projectPath;
        }

        return null;
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

        var projectPath = FindProjectPath(projectId);
        if (projectPath == null)
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

        var projectPath = FindProjectPath(projectId);
        if (projectPath == null)
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
    /// Gets the full path for a project ID by searching all project roots.
    /// </summary>
    /// <param name="projectId">The project ID.</param>
    /// <returns>Full path to the project folder.</returns>
    /// <exception cref="DirectoryNotFoundException">Thrown when project is not found.</exception>
    public string GetProjectPath(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("Project ID cannot be empty.", nameof(projectId));

        return FindProjectPath(projectId)
            ?? throw new DirectoryNotFoundException($"Project '{projectId}' not found in any project root.");
    }

    private static bool IsValidProjectFolder(string path)
    {
        if (!Directory.Exists(path))
            return false;

        // Check for required files in .godmode subfolder
        var statusFile = Path.Combine(path, ".godmode", "status.json");
        return File.Exists(statusFile);
    }
}
