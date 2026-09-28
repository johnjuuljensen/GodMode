using System.Collections.Concurrent;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Voice.Tests;

/// <summary>Servers in memory: attention lists a test sets, and the replies and seen marks voice sends.</summary>
internal sealed class FakeServers : IGodModeServers
{
    private readonly ConcurrentDictionary<string, (string Name, AttentionItem[] Items)> _lists = new();
    private readonly ConcurrentDictionary<ProjectRef, ProjectStatus> _statuses = new();

    public event Action<string, string, IReadOnlyList<AttentionItem>>? AttentionChanged;

    public ConcurrentQueue<(ProjectRef Project, string Text)> Replies { get; } = new();
    public ConcurrentQueue<ProjectRef> Seen { get; } = new();

    /// <summary>Sets the server's list and pushes it, as the hub does on a change.</summary>
    public void Set(string serverId, params AttentionItem[] items)
    {
        _lists[serverId] = (serverId, items);
        foreach (var item in items)
            _statuses.TryAdd(new ProjectRef(serverId, item.ProjectId), Status(item));
        AttentionChanged?.Invoke(serverId, serverId, items);
    }

    public Task<IReadOnlyList<ServerAttentionItem>> GetAttentionAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ServerAttentionItem>>(
            [.. _lists.SelectMany(l => l.Value.Items.Select(i => new ServerAttentionItem(l.Key, l.Value.Name, i))).OrderBy(i => i.Item.Since)]);

    public Task<IReadOnlyList<ServerProject>> ListProjectsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ServerProject>>(
            [.. _statuses.Select(s => new ServerProject(s.Key.ServerId, s.Key.ServerId,
                new ProjectSummary(s.Value.Id, s.Value.Name, s.Value.State, s.Value.UpdatedAt, PendingPermission: s.Value.PendingPermission)))]);

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

    /// <summary>A project on a server, that no attention item names.</summary>
    public void AddProject(string serverId, string projectId, string name) =>
        _statuses[new ProjectRef(serverId, projectId)] = new ProjectStatus(projectId, name, ProjectState.Idle,
            DateTime.UtcNow, DateTime.UtcNow, null, new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0);

    public static AttentionItem Question(string projectId, string name, string text, int minutesAgo = 5) =>
        new(projectId, name, "Default", "root", AttentionKind.Question, DateTime.UtcNow.AddMinutes(-minutesAgo), text);

    public static AttentionItem Permission(string projectId, string name, string summary, int minutesAgo = 5) =>
        new(projectId, name, "Default", "root", AttentionKind.Permission, DateTime.UtcNow.AddMinutes(-minutesAgo), summary,
            Permission: new PendingPermission("req-1", "Bash", summary, DateTime.UtcNow));

    private static ProjectStatus Status(AttentionItem item) =>
        new(item.ProjectId, item.ProjectName,
            item.Kind == AttentionKind.Permission ? ProjectState.WaitingPermission : ProjectState.WaitingInput,
            item.Since, item.Since, item.Kind == AttentionKind.Question ? item.Text : null,
            new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0, PendingPermission: item.Permission);
}
