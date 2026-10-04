using GodMode.Server.Services;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Server.Tests;

/// <summary>Which input GodMode passes to claude, and which it refuses (#31), from the session's status alone.</summary>
public class SlashCommandsTests
{
    private static ProjectStatus Status(IReadOnlyList<string>? passed = null, IReadOnlyList<string>? claude = null) =>
        new("p/r/s", "s", ProjectState.Idle, DateTime.UtcNow, DateTime.UtcNow, null, new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0,
            SlashCommands: passed, ClaudeCommands: claude);

    [Theory]
    [InlineData("/clear", "clear")]
    [InlineData("  /compact keep the plan", "compact")]
    [InlineData("/anthropic-skills:docx\nmake it", "anthropic-skills:docx")]
    [InlineData("/tmp/x is full", null)]
    [InlineData("see /clear", null)]
    [InlineData("/", null)]
    [InlineData("", null)]
    public void CommandOf_IsTheLeadingSlashName(string input, string? expected) =>
        Assert.Equal(expected, SlashCommands.CommandOf(input));

    [Theory]
    [InlineData("/clear")]
    [InlineData("/compact")]
    [InlineData("/context")]
    [InlineData("/CLEAR")]
    [InlineData("/frobnicate the widget")]
    [InlineData("/tmp/x is full")]
    [InlineData("plain text")]
    public void TheSupportedCommands_AndTextThatIsNoCommand_ArePassed_BeforeClaudeHasListedAny(string input) =>
        Assert.Null(SlashCommands.WhyRefused(input, Status()));

    [Theory]
    [InlineData("/model opus", "model and effort")]
    [InlineData("/effort high", "model and effort")]
    [InlineData("/rename x", "names the session")]
    [InlineData("/config", "not one of the commands")]
    [InlineData("/login", "not one of the commands")]
    public void ClaudesOtherCommands_AreRefused_WithWhy(string input, string why) =>
        Assert.Contains(why, SlashCommands.WhyRefused(input, null));

    [Fact]
    public void TheSessionsSkills_ArePassed_AndACommandOnlyItsClaudeLists_IsRefused()
    {
        var status = Status(SlashCommands.Passed(["my-skill", "doctor"]), ["my-skill", "doctor", "brand-new", "clear"]);

        Assert.Null(SlashCommands.WhyRefused("/my-skill go", status));
        // A skill of a built-in's name is the skill
        Assert.Null(SlashCommands.WhyRefused("/doctor", status));
        Assert.NotNull(SlashCommands.WhyRefused("/brand-new", status));
    }

    [Fact]
    public void Passed_IsTheSupportedCommandsThenTheSkills_EachOnce() =>
        Assert.Equal(["clear", "compact", "context", "recap", "loop"], SlashCommands.Passed(["loop", "clear", "", "loop"]));
}
