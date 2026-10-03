using GodMode.ClientBase.Attention;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Relay.Tests;

/// <summary>
/// What a notification says (#454): a pending question says it is one, and its full text has every question,
/// its header, its options and their descriptions.
/// </summary>
public sealed class AttentionNotificationTextTests
{
    private static readonly DateTime Since = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    private static AttentionItem Asking(params QuestionItem[] questions) =>
        new("Default/root/p1", "p1", "Default", "root", AttentionKind.Question, Since,
            string.Join("\n", questions.Select(q => q.Question)), Question: new PendingQuestion("r1", questions, Since));

    private static readonly QuestionItem Repair = new("Repair the folder?", "Repair",
        [new("Yes, repair now", "Deletes the stale worktree folder and makes it again"), new("No, leave it", null)], false);

    private static readonly QuestionItem Issues = new("Which issues to file?", "File issues",
        [new("The crash", "Files the crash in the voice graph"), new("The typo", "Files the typo")], true);

    [Fact]
    public void A_single_question_says_it_asks_and_shows_each_option_with_its_description()
    {
        var item = Asking(Repair);

        Assert.Equal("Question · p1", AttentionNotificationText.Title(item));
        Assert.Equal("Asks you: Repair the folder?", AttentionNotificationText.Line(item));
        Assert.Equal(
            "Repair\nRepair the folder?\n• Yes, repair now — Deletes the stale worktree folder and makes it again\n• No, leave it\n\nTap to answer.",
            AttentionNotificationText.Body(item));
    }

    [Fact]
    public void Several_questions_show_every_one_with_its_header_options_and_descriptions()
    {
        var item = Asking(Repair, Issues);

        Assert.Equal("2 questions · p1", AttentionNotificationText.Title(item));
        Assert.Equal("Asks you: Repair the folder?", AttentionNotificationText.Line(item));
        var body = AttentionNotificationText.Body(item);
        Assert.Contains("1/2 · Repair\nRepair the folder?\n• Yes, repair now — Deletes the stale worktree folder and makes it again", body);
        Assert.Contains("2/2 · File issues · choose any\nWhich issues to file?\n• The crash — Files the crash in the voice graph\n• The typo — Files the typo", body);
    }

    [Fact]
    public void A_question_in_plain_text_says_it_asks()
    {
        var item = new AttentionItem("Default/root/p1", "p1", null, null, AttentionKind.Question, Since, "Merge it now?");

        Assert.Equal("Question · p1", AttentionNotificationText.Title(item));
        Assert.Equal("Asks you: Merge it now?", AttentionNotificationText.Line(item));
        Assert.Equal("Asks you: Merge it now?", AttentionNotificationText.Body(item));
    }

    [Theory]
    [InlineData(AttentionKind.Error, "Error · p1")]
    [InlineData(AttentionKind.Escalation, "Decision · p1")]
    public void Another_kind_says_its_text(AttentionKind kind, string title)
    {
        var item = new AttentionItem("Default/root/p1", "p1", null, null, kind, Since, "The build failed.");

        Assert.Equal(title, AttentionNotificationText.Title(item));
        Assert.Equal("The build failed.", AttentionNotificationText.Line(item));
        Assert.Equal("The build failed.", AttentionNotificationText.Body(item));
    }
}
