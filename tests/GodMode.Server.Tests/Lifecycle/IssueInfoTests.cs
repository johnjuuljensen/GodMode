using GodMode.FakeClaude;
using GodMode.Server.Services;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// A root's <c>issueInfo</c> script (#473): it prints an issue's title and labels, which voice checks the action it
/// reads back against. Run in the root, with the issue in <c>GODMODE_INPUT_ISSUE</c>, and read strictly.
/// </summary>
public sealed class IssueInfoTests
{
    private const string IssueScript = """
        $ErrorActionPreference = 'Stop'
        $labels = if ($env:GODMODE_INPUT_ISSUE -eq '471') { @('epic') } else { @('bug', 'voice') }
        [ordered]@{ title = "Issue $env:GODMODE_INPUT_ISSUE in $(Split-Path -Leaf (Get-Location))"; labels = [string[]]$labels } | ConvertTo-Json -Compress
        """;

    private static LifecycleHarness IssueRoot(string? script = IssueScript, IReadOnlyDictionary<string, string?>? settings = null)
    {
        var harness = new LifecycleHarness(new FakeScript().EmitInit().EmitResult(),
            rootConfig: script is null ? null : new Dictionary<string, object> { ["issueInfo"] = "issue-info.ps1" }, settings: settings);
        if (script is not null)
            File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "issue-info.ps1"), script);
        return harness;
    }

    [Fact]
    public async Task TheScript_GivesTheIssuesTitleAndLabels_RunInTheRoot()
    {
        await using var harness = IssueRoot();
        await harness.Projects.RecoverProjectsAsync();

        var epic = await harness.Connect("c1").DescribeIssueAsync(LifecycleHarness.ProfileName, LifecycleHarness.RootName, "471");
        var bug = await harness.Connect("c1").DescribeIssueAsync(LifecycleHarness.ProfileName, LifecycleHarness.RootName, " 473 ");

        Assert.Equal(["epic"], epic!.Labels);
        Assert.Equal($"Issue 471 in {Path.GetFileName(harness.RootPath)}", epic.Title);
        Assert.Equal(["bug", "voice"], bug!.Labels);
    }

    [Fact]
    public async Task ARootWithNoScript_DescribesNothing()
    {
        await using var harness = IssueRoot(script: null);
        await harness.Projects.RecoverProjectsAsync();

        Assert.Null(await harness.Connect("c1").DescribeIssueAsync(LifecycleHarness.ProfileName, LifecycleHarness.RootName, "471"));
    }

    [Theory]
    [InlineData("""Write-Output '["epic"]'""", "not an object")]
    [InlineData("""Write-Output '{"labels": "epic"}'""", "not an array")]
    [InlineData("""Write-Output '{"labels": [1]}'""", "not a string")]
    [InlineData("""Write-Output '{"colour": "red"}'""", "unknown property 'colour'")]
    [InlineData("throw 'no gh today'", "failed")]
    [InlineData("Start-Sleep -Seconds 60", "took longer")]
    public async Task AScriptThatPrintsAnythingButTheIssue_FailsSayingWhy(string script, string reason)
    {
        await using var harness = IssueRoot($"$ErrorActionPreference = 'Stop'\n{script}\n",
            new Dictionary<string, string?> { [ProjectManager.ListScriptTimeoutSetting] = "15" });
        await harness.Projects.RecoverProjectsAsync();

        // 15 s: pwsh can take seconds to start on a loaded machine, and a slow start is no timeout
        var failed = await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() =>
            harness.Connect("c1").DescribeIssueAsync(LifecycleHarness.ProfileName, LifecycleHarness.RootName, "471"));

        Assert.Contains(reason, failed.Message);
    }

    [Fact]
    public void AnEmptyObject_IsAnIssueWithNoTitleNorLabels()
    {
        var info = IssueInfoScript.Parse("{}");
        Assert.Null(info.Title);
        Assert.Empty(info.Labels);
    }
}
