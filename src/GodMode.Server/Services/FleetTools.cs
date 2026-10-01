using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodMode.Server.Auth;
using GodMode.Server.Hubs;
using GodMode.Server.Models;
using GodMode.Shared;
using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodMode.Server.Services;

/// <summary>
/// The fleet's tools, on <see cref="GodModeMcp.FleetPath"/>: what an overseer outside GodMode (the user's own
/// claude, with the server's credential) runs sessions with. Each does what its hub method does, through the
/// same <see cref="IProjectManager"/> call, so a session it starts is in the app's list like any other.
/// None answers a permission prompt or a question, deletes, forgets, adopts or writes config: those are the user's.
/// Each returns JSON text, as the hub's models serialize (<see cref="JsonDefaults"/>); a refusal is the tool's error, saying why.
/// </summary>
[McpServerToolType]
[Authorize(Policy = GodModeAuthExtensions.FleetPolicy)]
public sealed class FleetTools(IProjectManager projects, IHubContext<ProjectHub, IProjectHubClient> hub, ILogger<FleetTools> logger)
{
    /// <summary>The most turns <see cref="ReadAsync"/> gives.</summary>
    public const int MaxTurns = 20;

    /// <summary>Create inputs a fleet caller does not set: skip-permissions settles every prompt the user would, and the parent is <c>parent</c>.</summary>
    private static readonly string[] RefusedInputs = ["skipPermissions", CreateProjectRequest.ParentInput];

    /// <summary>One session in <see cref="ListSessionsAsync"/>.</summary>
    /// <param name="Needs">What it needs from the user, as its attention item says; null when nothing.</param>
    public sealed record SessionEntry(string Id, string Name, string? Profile, string? Root, string? Kind, string? Action,
        ProjectState State, string? ParentId, AttentionKind? Needs, string? PullRequestUrl);

    /// <summary>What a session waits on, in full, in <see cref="ReadAsync"/>.</summary>
    /// <param name="Text">The whole of it: the permission's summary, the question, the error, or the last result.</param>
    /// <param name="Detail">For a permission: everything the call would run (<see cref="IProjectHub.GetPermissionDetail"/>).</param>
    /// <param name="Question">For a question claude asked with AskUserQuestion: its questions and options.</param>
    public sealed record WaitingOn(AttentionKind Kind, string Text, string? Tool = null, string? Detail = null, PendingQuestion? Question = null);

    /// <summary>A session as <see cref="ReadAsync"/> gives it.</summary>
    public sealed record SessionRead(string Id, string Name, ProjectState State, string? Kind, string? ParentId,
        string? Model, string? Effort, WaitingOn? WaitingOn, string? PullRequestUrl, IReadOnlyList<AssistantReply> Replies);

    /// <summary>What <see cref="StartSessionAsync"/> made: the session, or, for an action that starts none, its script's message.</summary>
    public sealed record Started(string? Id = null, string? Name = null, ProjectState? State = null, string? Kind = null,
        string? ParentId = null, string? Model = null, string? Effort = null, string? Message = null);

    /// <summary>A session's state after <see cref="SendAsync"/>, <see cref="StopAsync"/> or <see cref="ResumeAsync"/>.</summary>
    public sealed record SessionState(string Id, ProjectState State);

    [McpServerTool(Name = "list_sessions", ReadOnly = true)]
    [Description("Every GodMode session on this server: its ID, name, profile, root, kind, action, state, parent, " +
        "what it needs from the user (Permission, Question, Error, Review, Finished; null for nothing) and its pull request.")]
    public async Task<string> ListSessionsAsync()
    {
        var needs = projects.GetAttention().ToDictionary(item => item.ProjectId, item => item);
        var sessions = (await projects.ListProjectsAsync())
            .Select(s => new SessionEntry(s.Id, s.Name, s.ProfileName, s.RootName, s.Kind, s.ActionName, s.State, s.ParentId,
                needs.GetValueOrDefault(s.Id)?.Kind, s.PullRequest?.Url))
            .OrderBy(s => s.Id, StringComparer.Ordinal);
        return Json(sessions);
    }

    [McpServerTool(Name = "list_roots", ReadOnly = true)]
    [Description("The server's profiles, and its roots with their actions: each action's name, description, input schema " +
        "(JSON Schema), model, effort, and whether it starts a session. start_session takes a profile, a root, an action and its inputs.")]
    public async Task<string> ListRootsAsync() =>
        Json(new { Profiles = await projects.ListProfilesAsync(), Roots = await projects.ListProjectRootsAsync() });

    [McpServerTool(Name = "start_session")]
    [Description("Starts a session as the app's create does: the root's action with its inputs (list_roots gives its schema). " +
        "model and effort override the action's. With parent (a session ID) the new session is that one's child; without it, it is top level. " +
        "Returns the new session, or, for an action that starts no session, its script's message. Its permission prompts go to the user.")]
    public async Task<string> StartSessionAsync(
        [Description("The profile the root is in")] string profile,
        [Description("The root's name")] string root,
        [Description("The action's name; omit for the root's default action")] string? action = null,
        [Description("The action's inputs, by its input schema (for example name and prompt)")] JsonObject? inputs = null,
        [Description("The model, overriding the action's (for example opus, sonnet, or a full model ID)")] string? model = null,
        [Description("The effort level, overriding the action's: low, medium, high, xhigh or max; empty for claude's own default")] string? effort = null,
        [Description("The ID of the session this one is a child of; omit for a top-level session")] string? parent = null)
    {
        var values = (inputs ?? []).ToDictionary(input => input.Key, input => JsonSerializer.SerializeToElement(input.Value));
        if (RefusedInputs.FirstOrDefault(values.ContainsKey) is { } refused)
            throw new McpException(refused == CreateProjectRequest.ParentInput
                ? $"Name the parent with parent, not the {refused} input."
                : $"The fleet cannot start a session with {refused}: its permission prompts are the user's.");
        if (model != null) values["model"] = JsonSerializer.SerializeToElement(model);
        if (effort != null) values["effort"] = JsonSerializer.SerializeToElement(effort);

        logger.LogInformation("Fleet starting a session in profile '{Profile}' root '{Root}' action '{Action}' (parent {Parent})",
            profile, root, action ?? "(default)", parent ?? "none");
        var result = await Refusing(() => projects.CreateProjectAsync(
            new CreateProjectRequest(profile, root, values, action, string.IsNullOrWhiteSpace(parent) ? null : parent)));
        if (result.Project is not { } status) return Json(new Started(Message: result.Message));

        // As the hub's CreateProject: the app lists it
        await hub.Clients.All.ProjectCreated(status);
        return Json(new Started(status.Id, status.Name, status.State, status.Kind, status.ParentId, status.Model, status.Effort));
    }

    [McpServerTool(Name = "send")]
    [Description("Sends the session a message, as the app's reply does: to its running claude, or it is resumed with it. " +
        "Refused while the session waits on a permission prompt or a question: those are the user's to answer, in the app.")]
    public async Task<string> SendAsync(
        [Description("The session's ID")] string session,
        [Description("The message")] string text)
    {
        logger.LogInformation("Fleet sending to {ProjectId}", session);
        await Refusing(() => projects.ReplyAndResumeAsync(session, text, answersPending: false));
        return await StateAsync(session);
    }

    [McpServerTool(Name = "read", ReadOnly = true)]
    [Description("Reads a session: its state, what it waits on in full (a permission prompt with what it would run, a question, " +
        "an error, a pull request's review, or a finished turn's result), and its last replies, oldest first " +
        "(turns, default 1, at most 20; the last may be unfinished while it works).")]
    public async Task<string> ReadAsync(
        [Description("The session's ID")] string session,
        [Description("How many of its last turns to read")] int turns = 1)
    {
        if (turns is < 1 or > MaxTurns) throw new McpException($"turns must be 1 to {MaxTurns}.");
        var status = await Refusing(() => projects.GetStatusAsync(session));
        var replies = await Refusing(() => projects.LastRepliesAsync(session, turns));
        return Json(new SessionRead(status.Id, status.Name, status.State, status.Kind, status.ParentId, status.Model, status.Effort,
            await WaitingOnAsync(status), status.PullRequest?.Url, replies));
    }

    [McpServerTool(Name = "stop")]
    [Description("Stops the session, as the app's stop does: claude is interrupted, then ended. resume carries it on.")]
    public async Task<string> StopAsync([Description("The session's ID")] string session)
    {
        logger.LogInformation("Fleet stopping {ProjectId}", session);
        await Refusing(() => projects.StopProjectAsync(session));
        return await StateAsync(session);
    }

    [McpServerTool(Name = "resume")]
    [Description("Resumes a stopped session on its conversation, as the app's resume does. It is Idle until it is sent a message.")]
    public async Task<string> ResumeAsync([Description("The session's ID")] string session)
    {
        logger.LogInformation("Fleet resuming {ProjectId}", session);
        await Refusing(() => projects.ResumeProjectAsync(session));
        return await StateAsync(session);
    }

    /// <summary>What the session needs, from its attention item's kind, with the whole text where the item cuts it.</summary>
    private async Task<WaitingOn?> WaitingOnAsync(ProjectStatus status) => Attention.Of(status) switch
    {
        null => null,
        { Kind: AttentionKind.Permission, Permission: { } permission } => new WaitingOn(AttentionKind.Permission, permission.Summary,
            permission.ToolName, await DetailAsync(status.Id, permission.RequestId)),
        { Kind: AttentionKind.Question } => new WaitingOn(AttentionKind.Question,
            status.PendingQuestion is { } asked ? string.Join("\n", asked.Questions.Select(q => q.Question)) : status.CurrentQuestion ?? "",
            Question: status.PendingQuestion),
        { Kind: AttentionKind.Error } item => new WaitingOn(AttentionKind.Error, status.LastError ?? item.Text),
        { Kind: AttentionKind.Finished } item => new WaitingOn(AttentionKind.Finished, status.LastResult ?? item.Text),
        { } item => new WaitingOn(item.Kind, item.Text),
    };

    /// <summary>The permission's detail; null when it was answered or withdrawn since the status was read.</summary>
    private async Task<string?> DetailAsync(string projectId, string requestId)
    {
        try { return (await projects.GetPermissionDetailAsync(projectId, requestId)).Detail; }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException) { return null; }
    }

    private async Task<string> StateAsync(string projectId)
    {
        var status = await Refusing(() => projects.GetStatusAsync(projectId));
        return Json(new SessionState(status.Id, status.State));
    }

    /// <summary>
    /// A call whose failure is the tool's error, with its message, as the hub gives it its client: an MCP
    /// client gets only a generic message for any exception but <see cref="McpException"/>.
    /// </summary>
    private static async Task<T> Refusing<T>(Func<Task<T>> call)
    {
        try { return await call(); }
        catch (Exception ex) when (ex is not (McpException or OperationCanceledException)) { throw new McpException(ex.Message, ex); }
    }

    private static Task Refusing(Func<Task> call) => Refusing(async () => { await call(); return true; });

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonDefaults.Options);
}
