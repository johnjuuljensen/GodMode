using System.Collections.Concurrent;
using GodMode.Shared.Models;

namespace GodMode.Voice;

/// <summary>
/// Every server's projects as last heard, for as long as the session runs: each has a handle from the moment it is
/// heard of (at the start, or when it is created later, from the app or anywhere else), and a project that is deleted,
/// or whose server is let go of, is forgotten along with its handle. It is the only one that gives handles: the rest
/// of voice looks them up (<see cref="ProjectHandles.Of"/>), so no project it has not heard of gets one.
/// </summary>
public sealed class ProjectBoard
{
    private readonly ProjectHandles _handles;
    private readonly ConcurrentDictionary<string, IReadOnlyList<ServerProject>> _lists = new();
    private readonly Lock _lock = new();

    public ProjectBoard(IGodModeServers servers, ProjectHandles handles)
    {
        _handles = handles;
        servers.ProjectsChanged += Update;
    }

    /// <summary>A server's projects changed, and the handles with them.</summary>
    public event Action? Changed;

    /// <summary>Every server's projects, as last heard: the one changed last first.</summary>
    public IReadOnlyList<ServerProject> Projects =>
        [.. _lists.Values.SelectMany(l => l).OrderByDescending(p => p.Project.UpdatedAt)];

    /// <summary>The project, as last heard; null when no server has it.</summary>
    public ServerProject? Find(ProjectRef project) =>
        _lists.TryGetValue(project.ServerId, out var list) ? list.FirstOrDefault(p => p.Project.Id == project.ProjectId) : null;

    private void Update(string serverId, string serverName, IReadOnlyList<ProjectSummary> projects)
    {
        List<ServerProject> now = [.. projects.Select(p => new ServerProject(serverId, serverName, p))];
        lock (_lock)
        {
            var before = _lists.GetValueOrDefault(serverId) ?? [];
            _lists[serverId] = now;
            var ids = now.Select(p => p.Project.Id).ToHashSet();
            foreach (var gone in before.Where(p => !ids.Contains(p.Project.Id)))
                _handles.Forget(gone.Ref);
            foreach (var project in now)
                _handles.For(project.Ref, project.Project.Name, project.Project.RootName, project.Project.Kind);
        }
        Changed?.Invoke();
    }
}
