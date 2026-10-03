using System.Collections.Concurrent;
using System.Text.Json;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Voice.Tests;

/// <summary>
/// Servers in memory: attention lists, projects and roots a test sets, pushed as the hub pushes them, and the replies,
/// seen marks and creates voice sends.
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
    public ConcurrentQueue<(ServerRoot Root, string Action, IReadOnlyDictionary<string, string> Inputs)> Creates { get; } = new();
    private readonly ConcurrentQueue<ServerRoot> _roots = new();

    /// <summary>What a create waits on before it returns, as a create script takes its time; done at once when unset.</summary>
    public Task CreateGate { get; set; } = Task.CompletedTask;

    /// <summary>The server's error for a create, as a create script's failure comes back; none when unset.</summary>
    public string? CreateError { get; set; }

    /// <summary>A root on a server, with its actions, as <c>ListProjectRoots</c> lists it.</summary>
    public FakeServers AddRoot(string serverId, string name, string? profile, params CreateActionInfo[] actions)
    {
        _roots.Enqueue(new ServerRoot(serverId, serverId, new ProjectRootInfo(name, null, actions, profile)));
        _servers.TryAdd(serverId, 0);
        return this;
    }

    /// <summary>What listing the roots waits on, as a slow server's answer; done at once when unset.</summary>
    public Task RootsGate { get; set; } = Task.CompletedTask;

    /// <summary>How many times the roots were asked for.</summary>
    public int RootsListed => Volatile.Read(ref _rootsListed);
    private int _rootsListed;

    public async Task<IReadOnlyList<ServerRoot>> ListRootsAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _rootsListed);
        await RootsGate;
        return [.. _roots];
    }

    /// <summary>As the server creates: the session named as its action's templates name it, pushed as ProjectCreated.</summary>
    public async Task<CreateProjectResult> CreateAsync(ServerRoot root, string actionName, IReadOnlyDictionary<string, string> inputs, CancellationToken ct)
    {
        Creates.Enqueue((root, actionName, inputs));
        await CreateGate;
        if (CreateError is { } error)
            throw new InvalidOperationException(error);
        var name = inputs.TryGetValue("issueNumber", out var issue) ? $"issue_{issue}" : inputs.GetValueOrDefault("name", "session");
        var id = $"{root.Profile}/{root.Root.Name}/260930-{actionName}-{name}-{Creates.Count:x4}";
        AddProject(root.ServerId, id, name, root: root.Root.Name, kind: actionName, profile: root.Profile);
        return new CreateProjectResult(_statuses[new ProjectRef(root.ServerId, id)]);
    }

    /// <summary>An action whose form is <paramref name="schema"/>'s JSON; the server's default form (a name, a prompt) when null.</summary>
    public static CreateActionInfo Action(string name, string? schema = null, bool session = true) =>
        new(name, InputSchema: JsonSerializer.Deserialize<JsonElement>(schema ?? DefaultSchema), Session: session);

    public const string DefaultSchema = """
        { "type": "object", "properties": { "name": { "type": "string", "title": "Project Name" },
          "prompt": { "type": "string", "title": "Task Description" }, "skipPermissions": { "type": "boolean", "default": false } },
          "required": ["name"] }
        """;

    public const string IssueSchema = """
        { "type": "object", "properties": { "issueNumber": { "type": "string", "title": "Issue Number" },
          "baseBranch": { "type": "string", "title": "Base Branch" }, "skipPermissions": { "type": "boolean", "default": false } },
          "required": ["issueNumber"] }
        """;

    public const string BranchSchema = """
        { "type": "object", "properties": { "branch": { "type": "string", "title": "Branch" },
          "prompt": { "type": "string", "title": "Task Description" } }, "required": ["branch", "prompt"] }
        """;

    /// <summary>Sets the server's list and pushes it, as the hub does on a change, with the projects in it.</summary>
    public void Set(string serverId, params AttentionItem[] items)
    {
        _lists[serverId] = (serverId, items);
        foreach (var item in items)
            _statuses.TryAdd(new ProjectRef(serverId, item.ProjectId), Status(item));
        PushProjects(serverId);
        AttentionChanged?.Invoke(serverId, serverId, items);
    }

    /// <summary>Pushes an attention list alone, its projects unknown to the server's project list (a list on its own queue).</summary>
    public void PushAttention(string serverId, params AttentionItem[] items)
    {
        _lists[serverId] = (serverId, items);
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

    private readonly ConcurrentDictionary<ProjectRef, AssistantReply[]> _replies = new();

    /// <summary>The project's turns, oldest first, as its <c>output.jsonl</c> has them on the server.</summary>
    public void SetReplies(string serverId, string projectId, params AssistantReply[] replies) =>
        _replies[new ProjectRef(serverId, projectId)] = replies;

    /// <summary>The turns each read of replies asked for, in order.</summary>
    public ConcurrentQueue<(ProjectRef Project, int Turns)> RepliesRead { get; } = new();

    /// <summary>As the server reads them: the last <paramref name="turns"/>, oldest first; none for a project with none.</summary>
    public Task<IReadOnlyList<AssistantReply>> GetLastRepliesAsync(ProjectRef project, int turns, CancellationToken ct)
    {
        RepliesRead.Enqueue((project, turns));
        if (!_statuses.ContainsKey(project))
            return Task.FromException<IReadOnlyList<AssistantReply>>(new KeyNotFoundException(project.ProjectId));
        var all = _replies.GetValueOrDefault(project, []);
        return Task.FromResult<IReadOnlyList<AssistantReply>>(all[Math.Max(0, all.Length - turns)..]);
    }

    /// <summary>A project on a server, that no attention item names: created, as the hub pushes it.</summary>
    public void AddProject(string serverId, string projectId, string name, string? root = null, string? kind = null, string? profile = null)
    {
        _statuses[new ProjectRef(serverId, projectId)] = new ProjectStatus(projectId, name, ProjectState.Idle,
            DateTime.UtcNow, DateTime.UtcNow, null, new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0,
            RootName: root, ProfileName: profile, Kind: kind);
        PushProjects(serverId);
    }

    /// <summary>A project's status as the server holds it, in full, beside an attention item that cut its text.</summary>
    public void SetStatus(string serverId, ProjectStatus status)
    {
        _statuses[new ProjectRef(serverId, status.Id)] = status;
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

    public static AttentionItem Finished(string projectId, string name, string text, int minutesAgo = 5) =>
        new(projectId, name, "Default", "root", AttentionKind.Finished, DateTime.UtcNow.AddMinutes(-minutesAgo), text);

    public static AttentionItem Permission(string projectId, string name, string summary, int minutesAgo = 5) =>
        new(projectId, name, "Default", "root", AttentionKind.Permission, DateTime.UtcNow.AddMinutes(-minutesAgo), summary,
            Permission: new PendingPermission("req-1", "Bash", summary, DateTime.UtcNow));

    /// <summary>A create that failed before its launch (issue #448): Error, with no session to answer.</summary>
    public static AttentionItem CreateFailed(string projectId, string name, string reason, int minutesAgo = 5) =>
        new(projectId, name, "Default", "root", AttentionKind.Error, DateTime.UtcNow.AddMinutes(-minutesAgo), reason, CreateFailed: true);

    private void PushProjects(string serverId)
    {
        _servers.TryAdd(serverId, 0);
        ProjectsChanged?.Invoke(serverId, serverId, [.. _statuses.Where(s => s.Key.ServerId == serverId).Select(s => Summary(s.Value))]);
    }

    private static ProjectSummary Summary(ProjectStatus s) =>
        new(s.Id, s.Name, s.State, s.UpdatedAt, s.CurrentQuestion, s.RootName, s.ProfileName, s.PendingPermission, Kind: s.Kind);

    private static ProjectStatus Status(AttentionItem item) =>
        new(item.ProjectId, item.ProjectName,
            item.Kind switch
            {
                AttentionKind.Permission => ProjectState.WaitingPermission,
                AttentionKind.Error => ProjectState.Error,
                _ => ProjectState.WaitingInput,
            },
            item.Since, item.Since, item.Kind == AttentionKind.Question ? item.Text : null,
            new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0, RootName: item.Root, ProfileName: item.Profile,
            LastError: item.Kind == AttentionKind.Error ? item.Text : null,
            PendingPermission: item.Permission, CreateFailed: item.CreateFailed);
}
