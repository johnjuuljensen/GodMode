using GodMode.ProjectFiles;
using GodMode.Server.Models;
using GodMode.Shared;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using System.Diagnostics;
using System.Text.Json;

namespace GodMode.Server.Services;

/// <summary>
/// Updates status.json based on Claude output events and git status.
/// </summary>
public class StatusUpdater : IStatusUpdater
{
    private readonly ILogger<StatusUpdater> _logger;
    private readonly Dictionary<string, System.Timers.Timer> _gitPollingTimers = new();

    public StatusUpdater(ILogger<StatusUpdater> logger)
    {
        _logger = logger;
    }

    public async Task SaveStatusAsync(ProjectInfo project)
    {
        // A project without its state folder has nowhere to keep its status: a create that failed before
        // its script made the folder, or a folder removed outside GodMode. Making it would make the
        // folder, which a create script expects not to find. Its status is in memory until it is deleted
        var godModePath = project.StatePath;
        if (!Directory.Exists(godModePath))
        {
            _logger.LogDebug("Project {ProjectId} has no {Path}; its status is not saved", project.Status.Id, godModePath);
            return;
        }
        var statusPath = Path.Combine(godModePath, "status.json");

        var json = JsonSerializer.Serialize(project.Status, JsonDefaults.Options);

        // Atomic, so a reader (recovery, a restarted server) never meets a half-written file
        await AtomicFile.WriteAllTextAsync(statusPath, json);
    }

    public async Task<bool> UpdateFromOutputEventAsync(ProjectInfo project, OutputEvent outputEvent, string rawJson)
    {
        var stateChanged = false;
        var status = project.Status;
        var process = project.Process;
        // The session's activity (issue #468): each line of its main conversation, in memory only, as the output offset is
        if (IsConversationLine(outputEvent, rawJson))
            status = status with { LastOutputAt = DateTime.UtcNow };
        var lastOutputAt = status.LastOutputAt;
        // A result in a recap's shape, after a /recap, answers it (issue #513): a turn's result that comes first leaves the
        // mark for it
        var recapTurn = outputEvent.Type == OutputEventType.Result && IsRecapResult(outputEvent) && process.TakeRecap();
        // Every other result ends the turn the user's input started, if one did: a reply sent while the recap runs is
        // the turn after it
        var userTurn = outputEvent.Type == OutputEventType.Result && !recapTurn && process.TakeUserTurn();

        // Parse Claude output events to update state
        switch (outputEvent.Type)
        {
            case OutputEventType.User:
                // A new turn is starting — clear any memo of the previous turn's
                // trailing assistant text so stale questions don't leak forward.
                process.LastAssistantText = null;
                if (IsUsersMessage(outputEvent))
                {
                    // A message the user sent starts the reply over: the last turn's spoken reply, and one this
                    // turn gave before it, say nothing of the reply to come (issue #384)
                    process.ForgetSpoken();
                    if (status is not { SpokenSummary: null, Outcome: null })
                    {
                        status = status with { SpokenSummary = null, Outcome = null };
                        stateChanged = true;
                    }
                }
                // A recap is the session's standing, not the turn's reply: kept as soon as its call is, until another
                // replaces it, so no turn's start, end or error clears it (issue #466)
                else if (TakeSpeakResults(process, rawJson) is { } recap)
                {
                    status = status with { Recap = recap, RecapAt = DateTime.UtcNow };
                    stateChanged = true;
                }
                // claude has taken a message the user sent: it is working on it, whatever a result
                // of an earlier turn, handled after the send, said. It echoes it at once between
                // turns, and at its next step in one
                if (IsUsersMessage(outputEvent) && status is { PendingPermission: null, PendingQuestion: null }
                    && (status.State != ProjectState.Running || status.CurrentQuestion != null))
                {
                    status = status with { State = ProjectState.Running, CurrentQuestion = null };
                    stateChanged = true;
                }
                break;

            case OutputEventType.Assistant:
                // Remember the last text content block from this assistant event.
                // The deterministic question check happens on Result (end of turn).
                // Tool-only assistant events return null here; don't overwrite
                // a previously-seen text block in that case.
                var lastText = QuestionDetection.ExtractLastAssistantText(rawJson);
                if (lastText != null) process.LastAssistantText = lastText;
                foreach (var (toolUseId, call) in SpokenReply.Calls(rawJson))
                    process.SpeakCalls[toolUseId] = call;
                break;

            // Error events are stderr lines shown in the UI; the process's exit and error results
            // decide whether the session failed

            // claude's answer to the interrupt a stop sends: the stop decides the state
            case OutputEventType.Result when IsErrorResult(outputEvent) && process.Stopping:
                process.LastAssistantText = null;
                process.ForgetSpoken();
                break;

            case OutputEventType.Result when IsErrorResult(outputEvent):
                status = status with
                {
                    State = ProjectState.Error,
                    CurrentQuestion = null,
                    LastError = outputEvent.Content is { Length: > 0 } text ? text : Subtype(outputEvent) ?? "error result",
                    SpokenSummary = null,
                    Outcome = null,
                };
                process.LastAssistantText = null;
                process.ForgetSpoken();
                stateChanged = true;
                status = WithTokenMetrics(status, outputEvent);
                break;

            // /recap's answer (issue #513) is the session's standing, not a turn's reply: it is kept as the recap a speak call
            // gives, and the last reply, its spoken version and outcome stay, and nobody is told a turn finished
            case OutputEventType.Result when recapTurn:
                status = status with
                {
                    Recap = outputEvent.Content!.Trim(), RecapAt = DateTime.UtcNow, State = ProjectState.Idle, CurrentQuestion = null,
                };
                process.LastAssistantText = null;
                stateChanged = true;
                break;

            // A command's turn that says nothing (/clear, /compact): the session is idle, and has no new reply to
            // show, so the last one stays, and the user who sent it is not told it finished
            case OutputEventType.Result when IsSilentCommandResult(outputEvent):
                stateChanged = status.State != ProjectState.Idle || status.CurrentQuestion != null;
                status = WithTokenMetrics(status with { State = ProjectState.Idle, CurrentQuestion = null, LastError = null }, outputEvent);
                process.LastAssistantText = null;
                break;

            case OutputEventType.Result:
                // End of turn: decide Idle vs WaitingInput based on whether the
                // last assistant text block (trimmed) ends with '?'. See issue #131.
                // The result's text is claude's summary of the turn, whichever it is
                var endedAt = DateTime.UtcNow;
                // and its spoken version the one the session gave in it, or none (issue #384), and its outcome the one it
                // said, or none (issue #467)
                var outcome = process.Outcome;
                status = WithTurnEnd(status, IsQuietTurnEnd(project, userTurn, outcome)) with
                {
                    LastResult = outputEvent.Content, LastResultAt = endedAt, LastError = null, SpokenSummary = process.Spoken,
                    Outcome = outcome,
                };
                // A turn that says it needs the user, or is blocked, waits on the user as one that ended on a question does
                status = QuestionDetection.IsQuestion(process.LastAssistantText) || outcome is TurnOutcome.NeedsYou or TurnOutcome.Blocked
                    ? status with
                    {
                        State = ProjectState.WaitingInput, QuestionAt = endedAt,
                        CurrentQuestion = process.LastAssistantText ?? (outputEvent.Content is { Length: > 0 } said ? said : WaitsText(outcome)),
                    }
                    : status with { State = ProjectState.Idle, CurrentQuestion = null };
                process.LastAssistantText = null;
                process.ForgetSpoken();
                stateChanged = true;
                status = WithTokenMetrics(status, outputEvent);
                break;

            // What claude runs in the background (issue #432): the whole list, each time it changes, between turns too, and
            // an empty one when the last task has ended. Its steps are kept for the tasks it keeps
            case OutputEventType.System when Subtype(outputEvent) == BackgroundTasksChangedSubtype:
                if (BackgroundTasksOf(rawJson, status.BackgroundTasks) is { } tasks && !SameTasks(status.BackgroundTasks, tasks))
                {
                    status = status with { BackgroundTasks = tasks.Count == 0 ? null : tasks };
                    stateChanged = true;
                }
                break;

            // A background subagent's or workflow's step. A foreground one's, and a background task's own foreground
            // shells', are of no task in the list
            case OutputEventType.System when Subtype(outputEvent) == TaskProgressSubtype:
                if (WithStep(status.BackgroundTasks, rawJson) is { } stepped)
                {
                    status = status with { BackgroundTasks = stepped };
                    stateChanged = true;
                }
                break;

            case OutputEventType.System when IsSessionStart(outputEvent):
                // The session claude keeps is the one it reports, which a resume must name
                if (outputEvent.Metadata?.GetValueOrDefault(SessionIdKey) is string reported && !SessionIdFile.IsValid(reported))
                    _logger.LogWarning("Project {ProjectId} reported a session id that is not a GUID; it keeps {SessionId}",
                        project.Status.Id, project.ClaudeSessionId);
                else if (outputEvent.Metadata?.GetValueOrDefault(SessionIdKey) is string sessionId && sessionId != project.ClaudeSessionId)
                {
                    _logger.LogInformation("Project {ProjectId} runs session {SessionId} (asked for {Requested})",
                        project.Status.Id, sessionId, project.ClaudeSessionId);
                    project.ClaudeSessionId = sessionId;
                    // A write that fails is not the session failing: it is written again on the next init
                    try { await SessionIdFile.WriteAsync(project.StatePath, sessionId); }
                    catch (Exception ex) { _logger.LogError(ex, "Could not save the session id of project {ProjectId}", project.Status.Id); }
                }
                // The session (re)started - project is running
                stateChanged = status.State != ProjectState.Running || status.LastError != null;
                status = status with { State = ProjectState.Running, LastError = null };
                if (WithCommands(status, outputEvent) is { } withCommands)
                {
                    status = withCommands;
                    stateChanged = true;
                }
                break;

            case OutputEventType.ConversationReset:
                // /clear: the conversation that had the last reply and question is gone
                process.LastAssistantText = null;
                stateChanged = status is not { LastResult: null, LastResultAt: null, CurrentQuestion: null, QuietResult: false, UnseenResult: null, Outcome: null };
                status = status with { LastResult = null, LastResultAt = null, CurrentQuestion = null, QuietResult = false, UnseenResult = null, Outcome = null };
                break;
        }

        // Most lines (assistant text, tool use, echoed user messages) change nothing on disk: status.json carries their
        // time when something else changes
        if (!stateChanged)
        {
            project.Status = project.Status with { LastOutputAt = lastOutputAt };
            return false;
        }

        // Update duration
        var duration = DateTime.UtcNow - status.CreatedAt;
        status = status with { Metrics = status.Metrics with { Duration = duration } };

        // Update cost estimate (rough calculation: $3/M input tokens, $15/M output tokens for Claude Opus)
        var inputCost = (status.Metrics.InputTokens / 1_000_000m) * 3m;
        var outputCost = (status.Metrics.OutputTokens / 1_000_000m) * 15m;
        status = status with { Metrics = status.Metrics with { CostEstimate = inputCost + outputCost } };

        project.Status = status with { UpdatedAt = DateTime.UtcNow };
        return true;
    }

    /// <summary>
    /// Whether the turn ending now raises no Finished (issues #401, #467): the one place that decides it. Its action has
    /// <c>quietTurns</c> (<see cref="ProjectInfo.QuietTurns"/>), and the user did not start the turn
    /// (<paramref name="userTurn"/>): an overseer woken by a worker's message, a notice or its own background task, which
    /// the server cannot always tell apart, so the setting is the action's, not the turn's origin's. Or the session said
    /// the turn is <see cref="TurnOutcome.Continuing"/>: it carries on by itself, and nothing is the user's yet. A turn
    /// that said it is done is quiet still on a quiet action, whose setting decides for every turn the user did not start.
    /// </summary>
    public static bool IsQuietTurnEnd(ProjectInfo project, bool userTurn, TurnOutcome? outcome = null) =>
        project.QuietTurns && !userTurn || outcome == TurnOutcome.Continuing;

    /// <summary>What a turn that needs the user, or is blocked, asks, when it ended with no text at all.</summary>
    private static string WaitsText(TurnOutcome? outcome) =>
        outcome == TurnOutcome.Blocked ? "The session is blocked." : "The session needs you.";

    /// <summary>
    /// <paramref name="status"/> as a turn's end leaves its Finished: a quiet one keeps the
    /// unseen result of the last turn that raised one (<see cref="ProjectStatus.UnseenResult"/>), which its own result
    /// would otherwise replace; any other turn's end raises its own.
    /// </summary>
    private static ProjectStatus WithTurnEnd(ProjectStatus status, bool quiet)
    {
        if (!quiet) return status with { QuietResult = false, UnseenResult = null };
        var unseen = status.QuietResult
            ? status.UnseenResult
            : status.LastResultAt is { } at ? new TurnResult(at, status.LastResult, status.SpokenSummary, status.Outcome) : null;
        return status with { QuietResult = true, UnseenResult = unseen is { } kept && kept.At > (status.SeenAt ?? DateTime.MinValue) ? kept : null };
    }

    /// <summary>
    /// The results a user line gives this turn's <c>speak</c> calls: an accepted one's text is the turn's spoken reply,
    /// the last accepted the one kept, with its outcome, which a later accepted call without one leaves as it is; a refused
    /// or denied one gives none. Returns the recap of the last accepted call that gave one, or null.
    /// </summary>
    private static string? TakeSpeakResults(ProjectProcess process, string rawJson)
    {
        if (process.SpeakCalls.Count == 0) return null;
        string? recap = null;
        foreach (var (toolUseId, isError) in SpokenReply.Results(rawJson))
        {
            if (!process.SpeakCalls.Remove(toolUseId, out var call) || isError) continue;
            // The tool checked them as the stream has them; a call it would refuse is never said, nor its recap kept
            var (outcome, outcomeRefused) = SpeakTool.CheckOutcome(call.Outcome);
            if (SpeakTool.Check(call.Text).Refused is not null || SpeakTool.CheckRecap(call.Recap).Refused is not null
                || outcomeRefused is not null) continue;
            process.Spoken = call.Text;
            process.Outcome = outcome ?? process.Outcome;
            recap = call.Recap ?? recap;
        }
        return recap;
    }

    /// <summary>The metadata key a <c>system</c> event carries claude's session ID under.</summary>
    public const string SessionIdKey = "session_id";

    /// <summary>The metadata key a <c>system/init</c> carries claude's slash commands under (its <c>slash_commands</c>).</summary>
    public const string SlashCommandsKey = "slash_commands";

    /// <summary>The metadata key a <c>system/init</c> carries claude's skills under (its <c>skills</c>).</summary>
    public const string SkillsKey = "skills";

    /// <summary>The metadata key a <c>result</c> carries its <c>num_turns</c> under.</summary>
    public const string NumTurnsKey = "num_turns";

    /// <summary>
    /// The status with the commands a <c>system/init</c> listed (<see cref="ProjectStatus.SlashCommands"/>,
    /// <see cref="ProjectStatus.ClaudeCommands"/>); null when it lists none, or the same as the status has.
    /// </summary>
    private static ProjectStatus? WithCommands(ProjectStatus status, OutputEvent init)
    {
        var claude = init.Metadata?.GetValueOrDefault(SlashCommandsKey) as IReadOnlyList<string>;
        var skills = init.Metadata?.GetValueOrDefault(SkillsKey) as IReadOnlyList<string>;
        if (claude == null && skills == null) return null;
        var passed = SlashCommands.Passed(skills ?? []);
        return Same(status.SlashCommands, passed) && Same(status.ClaudeCommands, claude)
            ? null
            : status with { SlashCommands = passed, ClaudeCommands = claude ?? status.ClaudeCommands };

        static bool Same(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
            a == null ? b == null : b != null && a.SequenceEqual(b, StringComparer.Ordinal);
    }

    /// <summary>
    /// A result that ends a turn in which the model took no turn (<c>num_turns</c> 0) and that has no text: what claude
    /// writes after <c>/clear</c> or <c>/compact</c>. <c>/context</c>'s has its text, and is a reply as any other.
    /// </summary>
    private static bool IsSilentCommandResult(OutputEvent outputEvent) =>
        outputEvent.Metadata?.GetValueOrDefault(NumTurnsKey) is 0L && string.IsNullOrWhiteSpace(outputEvent.Content);

    /// <summary>
    /// The result of a <c>/recap</c> (issue #513): no error, the model took no turn (<c>num_turns</c> 0), and its text is
    /// the recap. claude writes a synthetic assistant line with the same text before it.
    /// </summary>
    private static bool IsRecapResult(OutputEvent outputEvent) =>
        !IsErrorResult(outputEvent) && outputEvent.Metadata?.GetValueOrDefault(NumTurnsKey) is 0L
        && !string.IsNullOrWhiteSpace(outputEvent.Content);

    /// <summary>
    /// A message the user sent, echoed (<see cref="IsEcho"/>): not a <c>/recap</c>, which asks the session where it stands
    /// and starts no turn of the user's (issue #513).
    /// </summary>
    private static bool IsUsersMessage(OutputEvent outputEvent) =>
        IsEcho(outputEvent) && !SlashCommands.IsRecap(outputEvent.Content);

    /// <summary>The metadata key a <c>user</c> event that claude echoed (<c>isReplay</c>) carries.</summary>
    public const string IsReplayKey = "is_replay";

    /// <summary>A user message the user sent, echoed by claude as it takes it (<c>--replay-user-messages</c>).</summary>
    private static bool IsEcho(OutputEvent outputEvent) =>
        outputEvent.Metadata?.GetValueOrDefault(IsReplayKey) is true;

    /// <summary>
    /// A line of the session's main conversation (issue #468): a message of the user's or the model's, or a turn's end. Not
    /// a system line, and not a subagent's, which names the tool use it runs under (<c>parent_tool_use_id</c>). Only a
    /// line that gives it a value other than null is parsed to tell (#507): claude writes it, as null, on nearly every line.
    /// </summary>
    internal static bool IsConversationLine(OutputEvent outputEvent, string rawJson)
    {
        if (outputEvent.Type is not (OutputEventType.User or OutputEventType.Assistant or OutputEventType.Result)) return false;
        if (NoParentToolUse(rawJson)) return true;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            return !(doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private const string ParentToolUseKey = "\"parent_tool_use_id\"";

    /// <summary>
    /// Whether every <c>"parent_tool_use_id"</c> in the line, if any, is followed by <c>: null</c>, read without parsing
    /// it: such a line is the main conversation's. False for one that may name a tool use, which is parsed to tell.
    /// </summary>
    internal static bool NoParentToolUse(string rawJson)
    {
        for (var at = rawJson.IndexOf(ParentToolUseKey, StringComparison.Ordinal); at >= 0;
             at = rawJson.IndexOf(ParentToolUseKey, at + ParentToolUseKey.Length, StringComparison.Ordinal))
        {
            var i = SkipWhitespace(rawJson, at + ParentToolUseKey.Length);
            if (i >= rawJson.Length || rawJson[i] != ':') return false;
            i = SkipWhitespace(rawJson, i + 1);
            if (string.CompareOrdinal(rawJson, i, "null", 0, 4) != 0) return false;
        }
        return true;
    }

    private static int SkipWhitespace(string text, int at)
    {
        while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
        return at;
    }

    /// <summary>The <c>system</c> subtype claude lists its background tasks with, whole, each time the list changes.</summary>
    public const string BackgroundTasksChangedSubtype = "background_tasks_changed";

    /// <summary>The <c>system</c> subtype a subagent's or workflow's task reports its current step with.</summary>
    public const string TaskProgressSubtype = "task_progress";

    /// <summary>
    /// The tasks a <c>system/background_tasks_changed</c> lists, each with the step <paramref name="before"/> had for it;
    /// empty when it lists none, null when the line cannot be read as one, which changes nothing.
    /// </summary>
    internal static IReadOnlyList<BackgroundTask>? BackgroundTasksOf(string rawJson, IReadOnlyList<BackgroundTask>? before)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (!doc.RootElement.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array) return null;
            return [.. tasks.EnumerateArray()
                .Where(task => task.ValueKind == JsonValueKind.Object && StringOf(task, "task_id") is { Length: > 0 })
                .Select(task =>
                {
                    var id = StringOf(task, "task_id")!;
                    return new BackgroundTask(id, StringOf(task, "task_type") ?? "", StringOf(task, "description") ?? "",
                        before?.FirstOrDefault(t => t.Id == id)?.Step);
                })];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="tasks"/> with the step a <c>system/task_progress</c> gives its task; null when it is of no task
    /// in them, cannot be read, or gives the step the task has.
    /// </summary>
    internal static IReadOnlyList<BackgroundTask>? WithStep(IReadOnlyList<BackgroundTask>? tasks, string rawJson)
    {
        if (tasks is not { Count: > 0 }) return null;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (StringOf(doc.RootElement, "task_id") is not { } id || StringOf(doc.RootElement, "description") is not { Length: > 0 } step)
                return null;
            return tasks.Any(t => t.Id == id && t.Step != step)
                ? [.. tasks.Select(t => t.Id == id ? t with { Step = step } : t)]
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool SameTasks(IReadOnlyList<BackgroundTask>? before, IReadOnlyList<BackgroundTask> after) =>
        before == null ? after.Count == 0 : before.SequenceEqual(after);

    private static string? StringOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary><c>system/init</c>: claude (re)started its session. It writes it once it has read its first input.</summary>
    public static bool IsSessionStart(OutputEvent outputEvent) =>
        outputEvent.Type == OutputEventType.System && Subtype(outputEvent) == "init";

    private static string? Subtype(OutputEvent outputEvent) =>
        outputEvent.Metadata?.GetValueOrDefault("subtype") as string;

    /// <summary><c>is_error</c>, or for a CLI that omits it, a subtype other than <c>success</c>.</summary>
    private static bool IsErrorResult(OutputEvent outputEvent) =>
        outputEvent.Metadata?.GetValueOrDefault("is_error") is bool isError
            ? isError
            : Subtype(outputEvent) is { } subtype && subtype != "success";

    /// <summary>Takes the token counts from a result's metadata.</summary>
    private static ProjectStatus WithTokenMetrics(ProjectStatus status, OutputEvent outputEvent)
    {
        if (outputEvent.Metadata == null) return status;

        if (outputEvent.Metadata.TryGetValue("input_tokens", out var inputTokens) && long.TryParse(inputTokens?.ToString(), out var input))
            status = status with { Metrics = status.Metrics with { InputTokens = input } };

        if (outputEvent.Metadata.TryGetValue("output_tokens", out var outputTokens) && long.TryParse(outputTokens?.ToString(), out var output))
            status = status with { Metrics = status.Metrics with { OutputTokens = output } };

        return status;
    }

    public async Task UpdateGitStatusAsync(ProjectInfo project)
    {
        if (!Directory.Exists(Path.Combine(project.ProjectPath, ".git")))
        {
            return;
        }

        try
        {
            // Get current branch
            var branch = await RunGitCommandAsync(project.ProjectPath, "rev-parse --abbrev-ref HEAD");

            // Get last commit
            var lastCommit = await RunGitCommandAsync(project.ProjectPath, "log -1 --format=%H");

            // Get status
            var statusOutput = await RunGitCommandAsync(project.ProjectPath, "status --porcelain");
            var lines = statusOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            var uncommittedChanges = 0;
            var untrackedFiles = 0;

            foreach (var line in lines)
            {
                if (line.StartsWith("??"))
                {
                    untrackedFiles++;
                }
                else
                {
                    uncommittedChanges++;
                }
            }

            project.Status = project.Status with
            {
                Git = new GitStatus(
                    branch.Trim(),
                    lastCommit.Trim(),
                    uncommittedChanges,
                    untrackedFiles
                )
            };

            await SaveStatusAsync(project);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update git status for project {ProjectId}", project.Status.Id);
        }
    }

    private async Task<string> RunGitCommandAsync(string workPath, string arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        return output;
    }

    public void StartGitPolling(ProjectInfo project, TimeSpan interval)
    {
        if (_gitPollingTimers.ContainsKey(project.Status.Id))
        {
            return;
        }

        var timer = new System.Timers.Timer(interval.TotalMilliseconds);
        timer.Elapsed += async (sender, e) =>
        {
            await UpdateGitStatusAsync(project);
        };
        timer.Start();

        _gitPollingTimers[project.Status.Id] = timer;
    }

    public void StopGitPolling(string projectId)
    {
        if (_gitPollingTimers.TryGetValue(projectId, out var timer))
        {
            timer.Stop();
            timer.Dispose();
            _gitPollingTimers.Remove(projectId);
        }
    }
}
