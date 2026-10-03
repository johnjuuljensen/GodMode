using System.Text;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.ClientBase.Attention;

/// <summary>
/// What a notification of an attention item says: its title, its one line, and its full text. A pending
/// AskUserQuestion says it is a question to answer, and its full text has every question with its header, its
/// options and their descriptions, so the user can read what they choose (#454). A tap opens the item in the app.
/// </summary>
public static class AttentionNotificationText
{
    public static string Title(AttentionItem item) => item switch
    {
        { Kind: AttentionKind.Question, Question.Questions.Count: > 1 and var count } => $"{count} questions · {item.ProjectName}",
        _ => $"{KindLabel(item.Kind)} · {item.ProjectName}",
    };

    /// <summary>The one line shown collapsed.</summary>
    public static string Line(AttentionItem item) => item switch
    {
        { Kind: AttentionKind.Question, Question.Questions: [var first, ..] } => $"Asks you: {first.Question}",
        { Kind: AttentionKind.Question } => $"Asks you: {item.Text}",
        _ => item.Text,
    };

    /// <summary>The whole text, shown expanded.</summary>
    public static string Body(AttentionItem item)
    {
        if (item is not { Kind: AttentionKind.Question, Question.Questions: { Count: > 0 } questions })
            return item.Kind == AttentionKind.Question ? Line(item) : item.Text;

        var text = new StringBuilder();
        for (var i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            if (i > 0) text.Append("\n\n");
            var heading = string.Join(" · ", new[]
            {
                questions.Count > 1 ? $"{i + 1}/{questions.Count}" : null,
                q.Header,
                q.MultiSelect ? "choose any" : null,
            }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (heading.Length > 0) text.Append(heading).Append('\n');
            text.Append(q.Question);
            foreach (var option in q.Options)
            {
                text.Append("\n• ").Append(option.Label);
                if (!string.IsNullOrWhiteSpace(option.Description)) text.Append(" — ").Append(option.Description);
            }
        }
        return text.Append("\n\nTap to answer.").ToString();
    }

    public static string KindLabel(AttentionKind kind) => kind switch
    {
        AttentionKind.Permission => "Permission",
        AttentionKind.Question => "Question",
        AttentionKind.Error => "Error",
        AttentionKind.Escalation => "Decision",
        AttentionKind.Review => "Changes requested",
        AttentionKind.Finished => "Finished",
        _ => kind.ToString(),
    };
}
