using System.ComponentModel;
using System.Security.Claims;
using GodMode.Server.Auth;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodMode.Server.Services;

/// <summary>
/// <c>compact</c>, on GodMode's own MCP endpoint beside <see cref="MessageParentTool"/> and <see cref="SpeakTool"/> (issue
/// #579): a session compacts itself at a clean break it can see, rather than at claude's limit in the middle of a task.
/// A session cannot compact in the middle of its own turn, so the call only schedules
/// (<see cref="IProjectManager.ScheduleCompaction"/>): when the turn ends, the server sends <c>/compact</c> with the
/// session's instructions, and when that is done, the session's continuation as its next input, labelled as its own
/// (<see cref="ContinuationOf"/>). Neither end raises a Finished: the session is not done, it goes on.
/// </summary>
[McpServerToolType]
[Authorize(Policy = GodModeAuthExtensions.ProjectPolicy)]
public sealed class CompactTool(IProjectManager projects, ILogger<CompactTool> logger)
{
    public const string Name = "compact";

    /// <summary>The label the continuation is sent under, on a line of its own, so it cannot pass for the user's words.</summary>
    public const string ContinuationLabel = "[Continuation, after a compaction the session asked for]";

    /// <summary>What the tool answers once it has scheduled the compaction.</summary>
    public const string Scheduled = "Compaction scheduled: end your turn now, with no more tool calls. When your turn has ended, " +
        "the conversation is compacted, then your continuation is sent to you as your next input.";

    [McpServerTool(Name = Name)]
    [Description("Compacts your conversation at a clean break, to save tokens on every later turn, then carries on with a " +
        "continuation you write now. Call it at a break you can see: after opening or updating a pull request, after a merge, " +
        "between dispatches, before starting an unrelated piece of work. Not in the middle of a task whose details are not " +
        "written down anywhere (an issue, a pull request, a progress file): after the compaction you have only its summary. " +
        "It only schedules: end your turn right after calling it, with no more tool calls (speak first, if you speak). When " +
        "your turn has ended, GodMode sends /compact with your instructions, then your continuation as your next input. " +
        "Refused while a compaction is pending, while the session is in a /compact it did not ask for, and outside a turn. " +
        "Input the user sends meanwhile cancels the continuation: the user has taken over. At most 8000 characters each.")]
    public string Compact(
        RequestContext<CallToolRequestParams> context,
        [Description("What you do next, in your own words, sent to you as your next input once the compaction is done: the " +
            "compacted session has only the summary, so name the issue, the pull request and where the plan is, e.g. " +
            "\"Carry on with issue 579: pull request 612 is open; check its review threads, then the next todo in " +
            ".autoclaude/progress.txt\"")] string continuation,
        [Description("Optional: what the summary must keep, sent as /compact <instructions>, e.g. \"keep the pull request " +
            "number, the open review threads and the plan in .autoclaude/progress.txt\"")] string? instructions = null)
    {
        var projectId = context.User?.FindFirstValue(GodModeAuthExtensions.ProjectIdClaim)
            ?? throw new McpException("The caller is not a project");
        try
        {
            projects.ScheduleCompaction(projectId, continuation, instructions);
            return Scheduled;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            logger.LogInformation("Project {ProjectId}: compact refused: {Reason}", projectId, ex.Message);
            throw new McpException(ex.Message, ex);
        }
    }

    /// <summary>The command the server sends once the turn has ended: <c>/compact</c>, then the instructions on its line.</summary>
    public static string CommandOf(string? instructions) =>
        string.IsNullOrWhiteSpace(instructions) ? $"/{SlashCommands.Compact}" : $"/{SlashCommands.Compact} {instructions.Trim()}";

    /// <summary>
    /// The continuation as the session reads it: its label, a line saying the compaction failed when it did
    /// (<paramref name="failure"/>), then the text.
    /// </summary>
    public static string ContinuationOf(string continuation, string? failure) =>
        failure == null
            ? $"{ContinuationLabel}\n{continuation}"
            : $"{ContinuationLabel}\nThe compaction failed ({SessionInbox.OneLine(failure, 300)}): the conversation was not compacted.\n{continuation}";
}
