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
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _rootsShown = new();
    private readonly Lock _lock = new();

    public ProjectBoard(IGodModeServers servers, ProjectHandles handles)
    {
        _handles = handles;
        servers.ProjectsChanged += Update;
        servers.RootsChanged += Retitle;
    }

    /// <summary>A server's projects changed, and the handles with them.</summary>
    public event Action? Changed;

    /// <summary>Every server's projects, as last heard: the one changed last first.</summary>
    public IReadOnlyList<ServerProject> Projects =>
        [.. _lists.Values.SelectMany(l => l).OrderByDescending(p => p.Project.UpdatedAt)];

    /// <summary>The project, as last heard; null when no server has it.</summary>
    public ServerProject? Find(ProjectRef project) =>
        _lists.TryGetValue(project.ServerId, out var list) ? list.FirstOrDefault(p => p.Project.Id == project.ProjectId) : null;

    /// <summary>
    /// The overseer that stands for the project in what voice says unasked (#469): the top of the chain of live parents the
    /// server recorded (<see cref="ProjectSummary.RecordedParentId"/>), the project itself when it has none. A parent is
    /// live while its server lists it, stopped or not: a deleted or forgotten one is not listed, and its children are
    /// top level again. The session's own <see cref="ProjectSummary.ParentId"/>, which it can write, decides nothing.
    /// </summary>
    public ServerProject TopOf(ServerProject project) => ParentsOf(project).LastOrDefault() ?? project;

    /// <summary>The project's live parents as the server recorded them, its own first, up to the top; a loop is cut where it closes.</summary>
    private IEnumerable<ServerProject> ParentsOf(ServerProject project)
    {
        HashSet<string> seen = [project.Project.Id];
        for (var at = project; at.Project.RecordedParentId is { } parent && seen.Add(parent) && Find(new ProjectRef(at.ServerId, parent)) is { } live; at = live)
            yield return live;
    }

    /// <summary>Whether a live overseer runs the project (<see cref="TopOf"/>): voice leaves it out unless the user asks for it.</summary>
    public bool IsRun(ProjectRef project) => Find(project) is { } found && ParentsOf(found).Any();

    /// <summary>The projects no live overseer runs (<see cref="IsRun"/>): what voice says unasked, the one changed last first.</summary>
    public IReadOnlyList<ServerProject> Shown => [.. Projects.Where(p => !ParentsOf(p).Any())];

    /// <summary>The projects the overseer runs: its workers, and theirs (#469). None for a project that runs none.</summary>
    public IReadOnlyList<ServerProject> WorkersOf(ProjectRef overseer) =>
        [.. Projects.Where(p => ParentsOf(p).Any(parent => parent.Ref == overseer))];

    /// <summary>
    /// What the project's root is shown as (#434): its title, or its name when it has none, or when another root of its
    /// profile on its server is shown as that title. Null when it is in no root.
    /// </summary>
    public string? RootShown(ServerProject project) =>
        project.Project.RootName is not { } root ? null
        : _rootsShown.TryGetValue(project.ServerId, out var shown) && shown.TryGetValue(RootKey(project.Project.ProfileName, root), out var title) ? title
        : root;

    private static string RootKey(string? profile, string root) => $"{profile ?? "Default"}/{root}";

    private void Retitle(string serverId, IReadOnlyList<ProjectRootInfo> roots)
    {
        var shown = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            // Two roots of a profile shown as one title are said by their names
            var clash = root.Title is { } title && roots.Any(other => other != root
                && string.Equals(other.ProfileName ?? "Default", root.ProfileName ?? "Default", StringComparison.OrdinalIgnoreCase)
                && string.Equals(other.Title ?? other.Name, title, StringComparison.OrdinalIgnoreCase));
            shown[RootKey(root.ProfileName, root.Name)] = clash ? root.Name : root.Title ?? root.Name;
        }
        lock (_lock)
        {
            _rootsShown[serverId] = shown;
            foreach (var project in _lists.GetValueOrDefault(serverId) ?? [])
                _handles.Retitle(project.Ref, RootShown(project));
        }
        Changed?.Invoke();
    }

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
            {
                _handles.For(project.Ref, project.Project.Name, project.Project.RootName, project.Project.Kind, project.Project.ProfileName);
                _handles.Retitle(project.Ref, RootShown(project));
            }
        }
        Changed?.Invoke();
    }
}
