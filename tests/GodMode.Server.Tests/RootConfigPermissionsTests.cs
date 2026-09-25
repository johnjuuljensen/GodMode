using GodMode.Server.Services;
using GodMode.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodMode.Server.Tests;

/// <summary>
/// <c>allowSkipPermissions</c> and <c>permissionMode</c> merge as the other scalar keys: an action's
/// overlay replaces the base. Skipping is off unless a root turns it on, and a mode claude does not
/// have, or <c>bypassPermissions</c>, is an error in the file that names it.
/// </summary>
public class RootConfigPermissionsTests
{
    private static RootConfig Read(bool strict, params (string File, string Json)[] files)
    {
        var root = ServerProcess.CreateWorkDir("permissionscfg");
        try
        {
            var dir = Path.Combine(root, ".godmode-root");
            Directory.CreateDirectory(dir);
            foreach (var (file, json) in files) File.WriteAllText(Path.Combine(dir, file), json);
            var reader = new RootConfigReader(NullLogger<RootConfigReader>.Instance);
            return strict ? reader.ReadConfigStrict(root) : reader.ReadConfig(root);
        }
        finally
        {
            ServerProcess.DeleteWorkDir(root);
        }
    }

    private static RootConfig Read(params (string File, string Json)[] files) => Read(strict: true, files);

    [Fact]
    public void Unset_DoesNotAllowSkip_AndHasNoMode()
    {
        var action = Read(("config.json", "{}")).ResolveAction(null)!;

        Assert.False(action.AllowSkipPermissions);
        Assert.Null(action.PermissionMode);
    }

    /// <summary>A root with no config at all is the default: it does not allow skipping either.</summary>
    [Fact]
    public void NoConfig_DoesNotAllowSkip()
    {
        Assert.False(Read().ResolveAction(null)!.AllowSkipPermissions);
    }

    [Fact]
    public void Overlay_ReplacesTheBase_KeyByKey()
    {
        var config = Read(
            ("config.json", """{ "allowSkipPermissions": true, "permissionMode": "acceptEdits" }"""),
            ("config.issue.json", """{ "allowSkipPermissions": false }"""),
            ("config.freeform.json", """{ "permissionMode": "auto" }"""),
            ("config.explore.json", "{}"));

        Assert.Equal((false, "acceptEdits"), Of(config.ResolveAction("issue")!));
        Assert.Equal((true, "auto"), Of(config.ResolveAction("freeform")!));
        Assert.Equal((true, "acceptEdits"), Of(config.ResolveAction("explore")!));

        static (bool, string?) Of(CreateAction action) => (action.AllowSkipPermissions, action.PermissionMode);
    }

    [Fact]
    public void Overlay_CanAllowWhatTheBaseDoesNot()
    {
        var config = Read(("config.json", "{}"), ("config.dev.json", """{ "allowSkipPermissions": true }"""));

        Assert.True(config.ResolveAction("dev")!.AllowSkipPermissions);
    }

    [Theory]
    [InlineData("acceptEdits")]
    [InlineData("auto")]
    [InlineData("manual")]
    [InlineData("dontAsk")]
    [InlineData("plan")]
    public void EachModeClaudeHas_IsKept(string mode)
    {
        Assert.Equal(mode, Read(("config.json", $$"""{ "permissionMode": "{{mode}}" }""")).ResolveAction(null)!.PermissionMode);
    }

    /// <summary>Spelled as claude spells it, whatever case the file uses: claude's own choices are case-sensitive.</summary>
    [Fact]
    public void Mode_IsSpelledAsClaudeSpellsIt()
    {
        Assert.Equal("dontAsk", Read(("config.json", """{ "permissionMode": "DONTASK" }""")).ResolveAction(null)!.PermissionMode);
    }

    [Theory]
    [InlineData("bypassPermissions", "permissionMode 'bypassPermissions' is refused: skipping permissions is allowSkipPermissions' to allow")]
    [InlineData("BypassPermissions", "permissionMode 'BypassPermissions' is refused")]
    [InlineData("default", "permissionMode 'default' is not one of acceptEdits, auto, manual, dontAsk, plan")]
    public void ModeTheRootMayNotPick_IsAnErrorInItsFile(string mode, string reason)
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            Read(("config.json", "{}"), ("config.issue.json", $$"""{ "permissionMode": "{{mode}}" }""")));

        Assert.StartsWith($"config.issue.json: {reason}", error.Message);
    }

    /// <summary>Read leniently (the roots listing), the action whose file is in error is left out, as an unreadable overlay is.</summary>
    [Fact]
    public void ModeTheRootMayNotPick_ReadLeniently_LeavesOutItsAction()
    {
        var config = Read(strict: false,
            ("config.json", "{}"),
            ("config.issue.json", """{ "permissionMode": "bypassPermissions" }"""),
            ("config.freeform.json", """{ "permissionMode": "auto" }"""));

        Assert.Equal(["freeform"], config.GetEffectiveActions().Select(a => a.Name));
    }
}
