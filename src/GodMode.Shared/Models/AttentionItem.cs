using GodMode.Shared.Enums;

namespace GodMode.Shared.Models;

/// <summary>
/// One project that needs the user, from <see cref="Hubs.IProjectHub.GetAttention"/> and
/// <see cref="Hubs.IProjectHubClient.AttentionChanged"/>. Answer any kind with
/// <see cref="Hubs.IProjectHub.ReplyAndResume"/>; a permission or question also with
/// <see cref="Hubs.IProjectHub.RespondToPermission"/> or <see cref="Hubs.IProjectHub.AnswerQuestion"/>.
/// </summary>
/// <param name="ProjectId">The project's ID, unique on its server (it contains '/').</param>
/// <param name="ProjectName">The project's name.</param>
/// <param name="Profile">The profile (account) the project belongs to.</param>
/// <param name="Root">The project root it belongs to.</param>
/// <param name="Kind">What it needs.</param>
/// <param name="Since">When it started to need it; the same after a server restart.</param>
/// <param name="Text">
/// Plain text to show or read aloud: the question, the permission's summary, the error, or the
/// result. Code blocks are left out and it is cut to about 500 characters; the transcript has it all.
/// </param>
/// <param name="Permission">The tool call to allow or deny, when <paramref name="Kind"/> is <see cref="AttentionKind.Permission"/>.</param>
/// <param name="Question">The AskUserQuestion with its options, when <paramref name="Kind"/> is <see cref="AttentionKind.Question"/> and claude asked with the tool; null for a question in plain text.</param>
/// <param name="PullRequestUrl">The project's pull request, when <paramref name="Kind"/> is <see cref="AttentionKind.Review"/> or <see cref="AttentionKind.Finished"/> and it has one.</param>
public record AttentionItem(
    string ProjectId,
    string ProjectName,
    string? Profile,
    string? Root,
    AttentionKind Kind,
    DateTime Since,
    string Text,
    PendingPermission? Permission = null,
    PendingQuestion? Question = null,
    string? PullRequestUrl = null);
