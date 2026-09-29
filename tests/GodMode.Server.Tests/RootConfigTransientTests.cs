using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Server.Tests.Lifecycle;
using GodMode.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodMode.Server.Tests;

/// <summary>
/// <c>transient</c> says an action's sessions are short-lived, so the app folds them sooner. It merges
/// as the other scalar keys: <c>config.json</c> gives it to every action of the root, an overlay
/// replaces it for its action. Off unless set, and listed with each action (<see cref="CreateActionInfo.Transient"/>).
/// </summary>
public class RootConfigTransientTests
{
    private static RootConfig Read(params (string File, string Json)[] files)
    {
        var root = ServerProcess.CreateWorkDir("transientcfg");
        try
        {
            var dir = Path.Combine(root, ".godmode-root");
            Directory.CreateDirectory(dir);
            foreach (var (file, json) in files) File.WriteAllText(Path.Combine(dir, file), json);
            return new RootConfigReader(NullLogger<RootConfigReader>.Instance).ReadConfigStrict(root);
        }
        finally
        {
            ServerProcess.DeleteWorkDir(root);
        }
    }

    [Fact]
    public void Unset_IsNotTransient()
    {
        Assert.False(Read(("config.json", "{}")).ResolveAction(null)!.Transient);
    }

    [Fact]
    public void TheRootsConfig_MakesEveryActionTransient_AndAnOverlayReplacesIt()
    {
        var config = Read(
            ("config.json", """{ "transient": true }"""),
            ("config.chat.json", "{}"),
            ("config.issue.json", """{ "transient": false }"""));

        Assert.True(config.ResolveAction("chat")!.Transient);
        Assert.False(config.ResolveAction("issue")!.Transient);
    }

    /// <summary>The list of roots carries it with each action, for the app to fold by.</summary>
    [Fact]
    public async Task ListedActions_SayWhetherTheyAreTransient()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin(),
            rootConfig: new Dictionary<string, object> { ["transient"] = true });

        var root = (await harness.Projects.ListProjectRootsAsync()).Single(r => r.Name == LifecycleHarness.RootName);

        Assert.True(Assert.Single(root.Actions!).Transient);
    }
}
