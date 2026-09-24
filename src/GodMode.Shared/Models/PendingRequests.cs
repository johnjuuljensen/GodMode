using System.Text.Json;

namespace GodMode.Shared.Models;

/// <summary>
/// A tool call claude is holding until the user allows or denies it, while the project is in
/// <see cref="Enums.ProjectState.WaitingPermission"/>. Answered with <see cref="Hubs.IProjectHub.RespondToPermission"/>.
/// </summary>
/// <param name="RequestId">Identifies the request to <see cref="Hubs.IProjectHub.RespondToPermission"/>.</param>
/// <param name="ToolName">The tool claude wants to run, for example <c>Bash</c> or <c>mcp__github__create_pull_request</c>.</param>
/// <param name="Input">The tool's input, as claude sent it.</param>
/// <param name="Summary">What the call does in one line, for example <c>Bash: git push origin feature/12-x</c> or <c>Edit: src/Foo.cs</c>. Read this rather than <paramref name="Input"/>.</param>
/// <param name="RequestedAt">When claude asked.</param>
public record PendingPermission(
    string RequestId,
    string ToolName,
    JsonElement Input,
    string Summary,
    DateTime RequestedAt);

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
