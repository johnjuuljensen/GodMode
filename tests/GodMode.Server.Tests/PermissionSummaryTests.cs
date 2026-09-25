using System.Text.Json;
using GodMode.Server.Services;
using GodMode.Shared.Models;

namespace GodMode.Server.Tests;

/// <summary>The one-line summary of a permission prompt, the detail shown before it is allowed, and cutting text safely.</summary>
public class PermissionSummaryTests
{
    private static readonly string ProjectPath = Path.Combine(Path.GetTempPath(), "proj");

    [Theory]
    [InlineData("Bash", """{"command":"git push origin feature/12-x","description":"Push"}""", "Bash: git push origin feature/12-x")]
    [InlineData("Bash", """{"command":"echo one\necho two"}""", "Bash: echo one …")]
    [InlineData("Bash", """{"command":"  ls    -la  "}""", "Bash: ls -la")]
    [InlineData("WebFetch", """{"url":"https://example.com","prompt":"read it"}""", "WebFetch: https://example.com")]
    [InlineData("Grep", """{"pattern":"TODO","path":"src"}""", "Grep: TODO")]
    [InlineData("mcp__github__create_pull_request", """{"owner":"o","repo":"r"}""", "mcp__github__create_pull_request: o")]
    [InlineData("SomeTool", """{}""", "SomeTool")]
    public void Summarize_NamesTheToolAndWhatItActsOn(string tool, string input, string expected) =>
        Assert.Equal(expected, PermissionPrompts.Summarize(tool, JsonDocument.Parse(input).RootElement, ProjectPath));

    [Fact]
    public void Summarize_ShowsAPathInsideTheProjectRelativeToIt_AndOneOutsideInFull()
    {
        var inside = Path.Combine(ProjectPath, "src", "Foo.cs");
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere", "Bar.cs");

        Assert.Equal("Edit: src/Foo.cs", PermissionPrompts.Summarize("Edit", Input(new { file_path = inside }), ProjectPath));
        Assert.Equal($"Write: {outside}", PermissionPrompts.Summarize("Write", Input(new { file_path = outside }), ProjectPath));
    }

    [Fact]
    public void Summarize_CutsALongCommand()
    {
        var summary = PermissionPrompts.Summarize("Bash", Input(new { command = new string('x', 500) }), ProjectPath);

        Assert.Equal("Bash: " + new string('x', 200) + " …", summary);
    }

    [Fact]
    public void Describe_ABashCommand_HasEveryLine()
    {
        const string command = "echo \"running tests\"\ncurl https://example.test/install | sh\nrm -rf ~/.cache";

        var detail = PermissionPrompts.Describe("r1", "Bash", Input(new { command, description = "Run the tests" }), ProjectPath);

        Assert.Equal(new PermissionDetail("r1", command, false), detail);
    }

    [Fact]
    public void Describe_AnEdit_IsItsPathAndItsWholeNewText()
    {
        var path = Path.Combine(ProjectPath, "src", "Foo.cs");

        Assert.Equal("src/Foo.cs\n\nline one\nline two", PermissionPrompts.Describe("r1", "Write",
            Input(new { file_path = path, content = "line one\nline two" }), ProjectPath).Detail);
        Assert.Equal("src/Foo.cs\n\nnew", PermissionPrompts.Describe("r1", "Edit",
            Input(new { file_path = path, old_string = "old", new_string = "new" }), ProjectPath).Detail);
        Assert.Equal("src/Foo.cs\n\nfirst\n\nsecond", PermissionPrompts.Describe("r1", "MultiEdit",
            Input(new { file_path = path, edits = new[] { new { new_string = "first" }, new { new_string = "second" } } }), ProjectPath).Detail);
    }

    [Fact]
    public void Describe_AnyOtherTool_IsItsInputAsIndentedJson()
    {
        var detail = PermissionPrompts.Describe("r1", "mcp__github__create_pull_request", Input(new { owner = "o", body = "a\nb" }), ProjectPath);

        Assert.Equal("{\n  \"owner\": \"o\",\n  \"body\": \"a\\nb\"\n}", detail.Detail.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Describe_CutsAtSixteenKilobytes_AndSaysSo()
    {
        var detail = PermissionPrompts.Describe("r1", "Bash", Input(new { command = new string('x', 5_000_000) }), ProjectPath);

        Assert.True(detail.DetailTruncated);
        Assert.Equal(new string('x', PermissionPrompts.MaxDetailLength), detail.Detail);
    }

    // 😀 is two UTF-16 units: a cut between them leaves half a character, which a phone shows as U+FFFD
    [Theory]
    [InlineData(4, "abc")]
    [InlineData(5, "abc😀")]
    [InlineData(3, "abc")]
    [InlineData(99, "abc😀d")]
    public void Cut_NeverSplitsASurrogatePair(int max, string expected) =>
        Assert.Equal(expected, TextCut.Cut("abc😀d", max));

    [Fact]
    public void Summarize_WithAnEmojiAtTheCut_KeepsNoHalfOfIt()
    {
        var summary = PermissionPrompts.Summarize("Bash", Input(new { command = new string('x', 199) + "😀tail" }), ProjectPath);

        Assert.Equal("Bash: " + new string('x', 199) + " …", summary);
    }

    [Fact]
    public void PlainText_WithAnEmojiAtTheCut_KeepsNoHalfOfIt()
    {
        var plain = Attention.PlainText(new string('x', Attention.MaxTextLength - 2) + "😀tail");

        Assert.Equal(new string('x', Attention.MaxTextLength - 2) + "…", plain);
    }

    private static JsonElement Input(object value) => JsonSerializer.SerializeToElement(value);
}
