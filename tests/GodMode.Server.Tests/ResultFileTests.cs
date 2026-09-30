using GodMode.Server.Services;
using GodMode.Server.Tests.Lifecycle;
using Microsoft.Extensions.Logging;

namespace GodMode.Server.Tests;

/// <summary>
/// A create script's result file: only its known keys are read, and a multi-line key
/// (<c>project_prompt</c>, <c>message</c>) runs to the end of the file whatever its lines hold.
/// </summary>
public sealed class ResultFileTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"godmode-result-{Guid.NewGuid():N}.result");
    private readonly CapturingLoggerProvider _logs = new();

    public void Dispose() => File.Delete(_path);

    private IReadOnlyDictionary<string, string> Read(params string[] lines)
    {
        File.WriteAllLines(_path, lines);
        return ProjectManager.ReadResultFile(_path, _logs.CreateLogger(nameof(ProjectManager)));
    }

    private static readonly string[] PromptWithEquals =
    [
        "Fix the parser.",
        "",
        "It reads a=b as a key,",
        "x = y too,",
        "and cuts https://example.com/search?q=1&r=2 short.",
        "kind=not-the-kind",
        "# a heading, kept",
        "The end.",
    ];

    private static string Joined(IEnumerable<string> lines) => string.Join(Environment.NewLine, lines);

    [Theory]
    [InlineData("project_prompt")]
    [InlineData("message")]
    public void AMultiLineKey_KeepsEveryLineAfterIt_WhateverTheyHold(string key)
    {
        var result = Read([$"{key}={PromptWithEquals[0]}", .. PromptWithEquals[1..]]);

        Assert.Equal(Joined(PromptWithEquals), result[key]);
        Assert.Equal([key], result.Keys);
        Assert.Empty(_logs.Lines);
    }

    [Fact]
    public void TheSingleLineKeysBeforeIt_AreStillRead()
    {
        var result = Read(["project_path=C:/roots/r/p", "project_name=Issue #362: cut", "kind=bug",
            $"project_prompt={PromptWithEquals[0]}", .. PromptWithEquals[1..]]);

        Assert.Equal("C:/roots/r/p", result["project_path"]);
        Assert.Equal("Issue #362: cut", result["project_name"]);
        Assert.Equal("bug", result["kind"]);
        Assert.Equal(Joined(PromptWithEquals), result["project_prompt"]);
    }

    [Fact]
    public void AnUnknownKey_IsIgnored_AndLoggedOnce()
    {
        var result = Read("project_name=p", "branch=bug/362", "branch=again", "a=b", "project_prompt=Go.");

        Assert.Equal(["project_name", "project_prompt"], result.Keys.Order());
        Assert.Equal("Go.", result["project_prompt"]);
        var logged = Assert.Single(_logs.Lines);
        Assert.Contains("branch, a", logged);
    }

    [Fact]
    public void AnIssueScriptsResult_ReadsAsBefore()
    {
        // The godmode-dev root's issue action: its path and name, with no trailing newline
        File.WriteAllText(_path, "project_path=C:/roots/godmode/bug-362\nproject_name=Issue #362: A create script's multi-line prompt");
        var result = ProjectManager.ReadResultFile(_path, _logs.CreateLogger(nameof(ProjectManager)));

        Assert.Equal("C:/roots/godmode/bug-362", result["project_path"]);
        Assert.Equal("Issue #362: A create script's multi-line prompt", result["project_name"]);
        Assert.Equal(2, result.Count);
        Assert.Empty(_logs.Lines);
    }

    [Fact]
    public void NoFile_IsNoResult() =>
        Assert.Empty(ProjectManager.ReadResultFile(_path, _logs.CreateLogger(nameof(ProjectManager))));
}
