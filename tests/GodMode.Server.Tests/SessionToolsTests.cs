using GodMode.FakeClaude;
using GodMode.Server.Services;
using static GodMode.Server.Tests.FleetRun;

namespace GodMode.Server.Tests;

/// <summary>
/// Every launch allows GodMode's own session tools, <c>message_parent</c>, <c>speak</c> and <c>compact</c>, so no session
/// asks the user for them (issues #384, #579): in one <c>--allowedTools</c> list, the root's own when its <c>claudeArgs</c> have one, which
/// keeps the root's tools (the fleet's, here).
/// </summary>
public class SessionToolsTests
{
    private const string Fleet = "mcp__godmode-fleet";

    /// <summary>The values of each <c>--allowedTools</c> in <paramref name="argv"/>: the arguments after it, up to the next flag.</summary>
    private static List<string[]> AllowedLists(IReadOnlyList<string> argv) =>
        [.. argv.Select((arg, i) => (arg, i)).Where(a => a.arg is "--allowedTools" or "--allowed-tools")
            .Select(a => argv.Skip(a.i + 1).TakeWhile(v => !v.StartsWith('-')).ToArray())];

    [Fact]
    public async Task ALaunchAllowsGodModesOwnTools_AndARootsOwnAllowedToolsKeepsItsTools_InOneList()
    {
        await using var run = await FleetRun.StartAsync(new FakeScript().EmitInit().AwaitStdin());
        run.WriteActionScript(OverseerAction, new FakeScript().EmitInit().AwaitStdin(),
            new() { ["fleetTools"] = true, ["claudeArgs"] = new[] { "--allowedTools", Fleet, "--verbose" } });

        var plain = await run.CreateOverHubAsync("plain");
        var plainLaunch = await run.WaitForLaunchAsync(plain, l => l.Stdin.Count > 0);
        Assert.Equal([[SessionTools.MessageParent, SpokenReply.ToolName, SessionTools.Compact]], AllowedLists(plainLaunch.Argv));

        var overseer = await run.CreateOverHubAsync("boss", OverseerAction);
        var overseerLaunch = await run.WaitForLaunchAsync(overseer, l => l.Stdin.Count > 0);
        Assert.Equal([[SessionTools.MessageParent, SpokenReply.ToolName, SessionTools.Compact, Fleet]], AllowedLists(overseerLaunch.Argv));
        Assert.Contains("--verbose", overseerLaunch.Argv);
        // The permission prompt is claude's own to call, and is not among them
        Assert.DoesNotContain(AllowedLists(overseerLaunch.Argv)[0], tool => tool.EndsWith(PermissionPromptTool.Name, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(new string[0], new[] { "--allowedTools", SessionTools.MessageParent, SpokenReply.ToolName, SessionTools.Compact })]
    [InlineData(new[] { "--verbose" }, new[] { "--verbose", "--allowedTools", SessionTools.MessageParent, SpokenReply.ToolName, SessionTools.Compact })]
    [InlineData(new[] { "--allowedTools", Fleet }, new[] { "--allowedTools", SessionTools.MessageParent, SpokenReply.ToolName, SessionTools.Compact, Fleet })]
    [InlineData(new[] { "--allowed-tools", Fleet }, new[] { "--allowed-tools", SessionTools.MessageParent, SpokenReply.ToolName, SessionTools.Compact, Fleet })]
    [InlineData(new[] { "--allowedTools=Bash(git:*)" }, new[] { "--allowedTools", SessionTools.MessageParent, SpokenReply.ToolName, SessionTools.Compact, "Bash(git:*)" })]
    [InlineData(new[] { "--allowedTools", Fleet, SessionTools.MessageParent }, new[] { "--allowedTools", SpokenReply.ToolName, SessionTools.Compact, Fleet, SessionTools.MessageParent })]
    [InlineData(new[] { "--allowedTools", SessionTools.MessageParent, SpokenReply.ToolName, SessionTools.Compact }, new[] { "--allowedTools", SessionTools.MessageParent, SpokenReply.ToolName, SessionTools.Compact })]
    public void Allow_AddsGodModesOwnTools_ToTheRootsList_OrOneOfTheirOwn(string[] claudeArgs, string[] expected) =>
        Assert.Equal(expected, SessionTools.Allow(claudeArgs));

    [Fact]
    public void Allow_WithNoArgs_GivesTheFlagAlone() =>
        Assert.Equal(["--allowedTools", SessionTools.MessageParent, SpokenReply.ToolName, SessionTools.Compact], SessionTools.Allow(null));
}
