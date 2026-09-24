using System.Text.Json;
using GodMode.Server.Services;
using Microsoft.Extensions.Configuration;

namespace GodMode.Server.Tests;

/// <summary>The one-line summary of a permission prompt, and the URL the bridge calls the server on.</summary>
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

    [Theory]
    [InlineData(null, "http://localhost:31337")]
    [InlineData("http://127.0.0.1:31337", "http://127.0.0.1:31337")]
    [InlineData("http://100.64.0.1:31337;http://127.0.0.1:4000", "http://127.0.0.1:4000")]
    [InlineData("http://+:31337", "http://localhost:31337")]
    [InlineData("http://0.0.0.0:5000", "http://localhost:5000")]
    [InlineData("http://localhost:6000", "http://localhost:6000")]
    public void LoopbackUrl_IsTheLoopbackBinding_OrLocalhostOnTheBindingsPort(string? urls, string expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Urls"] = urls })
            .Build();

        Assert.Equal(expected, ProjectManager.LoopbackUrl(configuration));
    }

    private static JsonElement Input(object value) => JsonSerializer.SerializeToElement(value);
}
