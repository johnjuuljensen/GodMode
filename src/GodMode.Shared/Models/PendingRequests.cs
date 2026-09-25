using System.Text.Json;

namespace GodMode.Shared.Models;

/// <summary>
/// A tool call claude is holding until the user allows or denies it, while the project is in
/// <see cref="Enums.ProjectState.WaitingPermission"/>. Answered with <see cref="Hubs.IProjectHub.RespondToPermission"/>.
/// </summary>
/// <param name="RequestId">Identifies the request to <see cref="Hubs.IProjectHub.RespondToPermission"/>.</param>
/// <param name="ToolName">The tool claude wants to run, for example <c>Bash</c> or <c>mcp__github__create_pull_request</c>.</param>
/// <param name="Summary">
/// What the call does in one line, for example <c>Bash: git push origin feature/12-x</c> or <c>Edit: src/Foo.cs</c>,
/// for a notification or to read aloud. It ends with " …" when it leaves something out (a second line, or
/// the rest of a long one): it is never enough to approve by, <see cref="Hubs.IProjectHub.GetPermissionDetail"/> is.
/// </param>
/// <param name="RequestedAt">When claude asked.</param>
/// <remarks>
/// The tool's input is not here: it can be megabytes (a <c>Write</c>), and this is pushed to every
/// client on every change. The server keeps it for as long as the request waits.
/// </remarks>
public record PendingPermission(
    string RequestId,
    string ToolName,
    string Summary,
    DateTime RequestedAt);

/// <summary>
/// Everything a <see cref="PendingPermission"/> would run, to show before it is allowed, from
/// <see cref="Hubs.IProjectHub.GetPermissionDetail"/>.
/// </summary>
/// <param name="RequestId">The request it describes.</param>
/// <param name="Detail">
/// The server's display text of the tool's input: the whole command for <c>Bash</c> and <c>PowerShell</c>,
/// the path and the whole new text for <c>Write</c> and <c>NotebookEdit</c>, the path and each replacement for
/// <c>Edit</c> and <c>MultiEdit</c> (what it replaces, with what, and whether every occurrence),
/// and the input as indented JSON for any other tool.
/// </param>
/// <param name="DetailTruncated">
/// Whether <paramref name="Detail"/> was cut at <c>16384</c> characters. The call runs with all of it:
/// say so, and let the user deny it or read the rest in the transcript.
/// </param>
public record PermissionDetail(
    string RequestId,
    string Detail,
    bool DetailTruncated);

/// <summary>
/// The user's answer to a <see cref="PendingPermission"/>.
/// </summary>
/// <param name="Allow">Whether the tool call may run.</param>
/// <param name="Message">Why it was denied, which claude reads. A default is used when null.</param>
/// <param name="UpdatedInput">The input to run the tool with instead of the one claude asked with. Only read when allowed.</param>
public record PermissionDecision(
    bool Allow,
    string? Message = null,
    JsonElement? UpdatedInput = null);

/// <summary>
/// Questions claude asked with its <c>AskUserQuestion</c> tool and is waiting on, while the project
/// is in <see cref="Enums.ProjectState.WaitingInput"/>. Answered with <see cref="Hubs.IProjectHub.AnswerQuestion"/>.
/// </summary>
/// <param name="RequestId">Identifies the request to <see cref="Hubs.IProjectHub.AnswerQuestion"/>.</param>
/// <param name="Questions">The questions, in the order claude asked them (one to four).</param>
/// <param name="RequestedAt">When claude asked.</param>
public record PendingQuestion(
    string RequestId,
    IReadOnlyList<QuestionItem> Questions,
    DateTime RequestedAt);

/// <summary>One question of a <see cref="PendingQuestion"/>.</summary>
/// <param name="Question">The question. The answer to it is keyed by this text.</param>
/// <param name="Header">A short label for it, for example <c>Auth method</c>.</param>
/// <param name="Options">The choices claude offers. The user may answer with other text too.</param>
/// <param name="MultiSelect">Whether more than one option may be chosen; the answer then joins their labels with ", ".</param>
public record QuestionItem(
    string Question,
    string? Header,
    IReadOnlyList<QuestionOption> Options,
    bool MultiSelect);

/// <summary>One choice of a <see cref="QuestionItem"/>.</summary>
public record QuestionOption(string Label, string? Description);
