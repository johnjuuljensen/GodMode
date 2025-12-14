using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.ProjectFiles;

/// <summary>
/// Manager for discovering and managing multiple project folders across named project roots.
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
    /// Lists all project roots.
    /// </summary>
    /// <returns>Array of project roots.</returns>
    public ProjectRoot[] ListProjectRoots()
    {
        return _projectRoots
            .Select(kvp => new ProjectRoot(kvp.Key, kvp.Value))
            .ToArray();
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
    /// Creates a new project in the specified project root.
    /// </summary>
    /// <param name="rootName">The name of the project root.</param>
    /// <param name="name">Human-readable project name. For worktree projects, this is also the branch name.</param>
    /// <param name="projectType">The type of project to create.</param>
    /// <param name="repoUrl">Repository URL (required for GitHubRepo and GitHubWorktree types).</param>
    /// <returns>A new ProjectFolder instance and the project ID.</returns>
    /// <exception cref="ArgumentException">Thrown when required parameters are missing.</exception>
    public (ProjectFolder Folder, string ProjectId) CreateProject(
        string rootName,
        string name,
        ProjectType projectType,
        string? repoUrl = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Project name cannot be empty.", nameof(name));

        var rootPath = GetProjectRootPath(rootName);

        // Validate repo URL for GitHub project types
        if (projectType is ProjectType.GitHubRepo or ProjectType.GitHubWorktree)
        {
            if (string.IsNullOrWhiteSpace(repoUrl))
                throw new ArgumentException($"Repository URL is required for {projectType} projects.", nameof(repoUrl));
        }

        // Convert name to path-safe project ID using the convention: space -> underscore
        var projectId = ConvertNameToPath(name);

        return projectType switch
        {
            ProjectType.RawFolder => CreateRawFolderProject(rootPath, projectId, name),
            ProjectType.GitHubRepo => CreateGitHubRepoProject(rootPath, projectId, name, repoUrl!),
            ProjectType.GitHubWorktree => CreateWorktreeProject(rootPath, projectId, name, repoUrl!),
            _ => throw new ArgumentException($"Unknown project type: {projectType}", nameof(projectType))
        };
    }

    /// <summary>
    /// Creates a raw folder project.
    /// </summary>
    private (ProjectFolder Folder, string ProjectId) CreateRawFolderProject(
        string rootPath,
        string projectId,
        string name)
    {
        var folder = ProjectFolder.Create(rootPath, projectId, name, repoUrl: null);
        return (folder, projectId);
    }

    /// <summary>
    /// Creates a GitHub repo project by cloning the repository.
    /// Note: Actual cloning is performed by the caller (Server) after creation.
    /// </summary>
    private (ProjectFolder Folder, string ProjectId) CreateGitHubRepoProject(
        string rootPath,
        string projectId,
        string name,
        string repoUrl)
    {
        var folder = ProjectFolder.Create(rootPath, projectId, name, repoUrl);
        return (folder, projectId);
    }

    /// <summary>
    /// Creates a worktree project.
    /// The project name is used as the branch name.
    /// The bare repo is stored at .{repoName}_bare if not existing.
    /// Note: Actual git operations are performed by the caller (Server) after creation.
    /// </summary>
    private (ProjectFolder Folder, string ProjectId) CreateWorktreeProject(
        string rootPath,
        string projectId,
        string name,
        string repoUrl)
    {
        // For worktree projects, the project ID is the branch name
        var folder = ProjectFolder.Create(rootPath, projectId, name, repoUrl);
        return (folder, projectId);
    }

    /// <summary>
    /// Converts a display name to a path-safe project ID.
    /// Convention: spaces become underscores.
    /// </summary>
    public static string ConvertNameToPath(string name)
    {
        return name.Replace(' ', '_');
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
    /// Gets the bare repository path for a worktree project.
    /// </summary>
    /// <param name="rootPath">The project root path.</param>
    /// <param name="repoUrl">The repository URL.</param>
    /// <returns>The path to the bare repository.</returns>
    public static string GetBareRepoPath(string rootPath, string repoUrl)
    {
        var repoName = GetRepoNameFromUrl(repoUrl);
        return Path.Combine(rootPath, $".{repoName}_bare");
    }

    /// <summary>
    /// Extracts the repository name from a Git URL.
    /// </summary>
    private static string GetRepoNameFromUrl(string repoUrl)
    {
        // Handle both HTTPS and SSH URLs
        // https://github.com/user/repo.git -> repo
        // git@github.com:user/repo.git -> repo
        var uri = repoUrl.TrimEnd('/');
        if (uri.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            uri = uri[..^4];

        var lastSlash = uri.LastIndexOf('/');
        var lastColon = uri.LastIndexOf(':');
        var lastSeparator = Math.Max(lastSlash, lastColon);

        return lastSeparator >= 0 ? uri[(lastSeparator + 1)..] : uri;
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
