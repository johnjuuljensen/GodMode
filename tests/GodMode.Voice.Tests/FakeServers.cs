using System.Collections.Concurrent;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Voice.Tests;

/// <summary>
/// Servers in memory: attention lists and projects a test sets, pushed as the hub pushes them, and the replies and
/// seen marks voice sends.
/// </summary>
internal sealed class FakeServers(params string[] serverIds) : IGodModeServers
{
    private readonly ConcurrentDictionary<string, (string Name, AttentionItem[] Items)> _lists = new();
    private readonly ConcurrentDictionary<ProjectRef, ProjectStatus> _statuses = new();
    private readonly ConcurrentDictionary<string, byte> _servers = new(serverIds.Select(id => KeyValuePair.Create(id, (byte)0)));

    public event Action<string, string, IReadOnlyList<AttentionItem>>? AttentionChanged;
    public event Action<string, string, IReadOnlyList<ProjectSummary>>? ProjectsChanged;

    public ConcurrentQueue<(ProjectRef Project, string Text)> Replies { get; } = new();
    public ConcurrentQueue<ProjectRef> Seen { get; } = new();

    /// <summary>Sets the server's list and pushes it, as the hub does on a change, with the projects in it.</summary>
    public void Set(string serverId, params AttentionItem[] items)
    {
        _lists[serverId] = (serverId, items);
        foreach (var item in items)
            _statuses.TryAdd(new ProjectRef(serverId, item.ProjectId), Status(item));
        PushProjects(serverId);
        AttentionChanged?.Invoke(serverId, serverId, items);
    }

    /// <summary>What a connection to every server hears first: each one's projects (VoiceSession's connect).</summary>
    public Task ConnectAsync(CancellationToken ct)
    {
        foreach (var serverId in _servers.Keys)
            PushProjects(serverId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ServerAttentionItem>> GetAttentionAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ServerAttentionItem>>(
            [.. _lists.SelectMany(l => l.Value.Items.Select(i => new ServerAttentionItem(l.Key, l.Value.Name, i))).OrderBy(i => i.Item.Since)]);

    public Task<ProjectStatus> GetStatusAsync(ProjectRef project, CancellationToken ct) =>
        _statuses.TryGetValue(project, out var status)
            ? Task.FromResult(status)
            : Task.FromException<ProjectStatus>(new KeyNotFoundException(project.ProjectId));

    public Task ReplyAsync(ProjectRef project, string text, CancellationToken ct)
    {
        Replies.Enqueue((project, text));
        return Task.CompletedTask;
    }

    public Task MarkSeenAsync(ProjectRef project, CancellationToken ct)
    {
        Seen.Enqueue(project);
        return Task.CompletedTask;
    }

    /// <summary>A project on a server, that no attention item names: created, as the hub pushes it.</summary>
    public void AddProject(string serverId, string projectId, string name, string? root = null, string? kind = null, string? profile = null)
    {
        _statuses[new ProjectRef(serverId, projectId)] = new ProjectStatus(projectId, name, ProjectState.Idle,
            DateTime.UtcNow, DateTime.UtcNow, null, new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0,
            RootName: root, ProfileName: profile, Kind: kind);
        PushProjects(serverId);
    }

    /// <summary>The project is deleted, as the hub pushes it.</summary>
    public void DeleteProject(string serverId, string projectId)
    {
        _statuses.TryRemove(new ProjectRef(serverId, projectId), out _);
        PushProjects(serverId);
    }

    public static AttentionItem Question(string projectId, string name, string text, int minutesAgo = 5) =>
        new(projectId, name, "Default", "root", AttentionKind.Question, DateTime.UtcNow.AddMinutes(-minutesAgo), text);

    public static AttentionItem Permission(string projectId, string name, string summary, int minutesAgo = 5) =>
        new(projectId, name, "Default", "root", AttentionKind.Permission, DateTime.UtcNow.AddMinutes(-minutesAgo), summary,
            Permission: new PendingPermission("req-1", "Bash", summary, DateTime.UtcNow));

    private void PushProjects(string serverId)
    {
        _servers.TryAdd(serverId, 0);
        ProjectsChanged?.Invoke(serverId, serverId, [.. _statuses.Where(s => s.Key.ServerId == serverId).Select(s => Summary(s.Value))]);
    }

    private static ProjectSummary Summary(ProjectStatus s) =>
        new(s.Id, s.Name, s.State, s.UpdatedAt, s.CurrentQuestion, s.RootName, s.ProfileName, s.PendingPermission, Kind: s.Kind);

    private static ProjectStatus Status(AttentionItem item) =>
        new(item.ProjectId, item.ProjectName,
            item.Kind == AttentionKind.Permission ? ProjectState.WaitingPermission : ProjectState.WaitingInput,
            item.Since, item.Since, item.Kind == AttentionKind.Question ? item.Text : null,
            new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0, RootName: item.Root, ProfileName: item.Profile,
            PendingPermission: item.Permission);
}
