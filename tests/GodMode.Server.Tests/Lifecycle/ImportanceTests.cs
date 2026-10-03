using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Shared;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// How much a session may interrupt the user (issue #438): its tier, quiet, normal or important, set at its create from its
/// action's <c>importance</c> and by the user since, kept in its settings.json; and each attention item's alert, from its
/// tier and kind. The tier never takes an item out of the user's list.
/// </summary>
public class ImportanceTests
{
    private static FakeScript Finishing(string result) =>
        new FakeScript().EmitInit().AwaitStdin().EmitAssistant("Working on it.").Sleep(50).EmitResult(result).AwaitStdin();

    private static Task WaitForResultAsync(LifecycleHarness harness, string projectId, string result) =>
        harness.WaitForStatusPushAsync(projectId, status => status.LastResult == result && status.State == ProjectState.Idle);

    private static ProjectFiles.ProjectSettings Settings(LifecycleHarness harness, string projectId) =>
        ProjectFiles.ProjectSettings.Load(harness.StatePath(projectId));

    // ── The alert of each kind and tier ──

    [Theory]
    // A quiet session's result, review and error are in the inbox alone
    [InlineData(Importance.Quiet, AttentionKind.Finished, AttentionAlert.Inbox)]
    [InlineData(Importance.Quiet, AttentionKind.Review, AttentionAlert.Inbox)]
    [InlineData(Importance.Quiet, AttentionKind.Error, AttentionAlert.Inbox)]
    // What blocks it still notifies
    [InlineData(Importance.Quiet, AttentionKind.Permission, AttentionAlert.Notify)]
    [InlineData(Importance.Quiet, AttentionKind.Question, AttentionAlert.Notify)]
    [InlineData(Importance.Quiet, AttentionKind.Escalation, AttentionAlert.Notify)]
    // Normal is what every session did before tiers
    [InlineData(Importance.Normal, AttentionKind.Finished, AttentionAlert.Notify)]
    [InlineData(Importance.Normal, AttentionKind.Review, AttentionAlert.Notify)]
    [InlineData(Importance.Normal, AttentionKind.Error, AttentionAlert.Notify)]
    [InlineData(Importance.Normal, AttentionKind.Permission, AttentionAlert.Notify)]
    [InlineData(Importance.Normal, AttentionKind.Question, AttentionAlert.Notify)]
    [InlineData(Importance.Normal, AttentionKind.Escalation, AttentionAlert.Notify)]
    // An important session's every item interrupts
    [InlineData(Importance.Important, AttentionKind.Finished, AttentionAlert.Interrupt)]
    [InlineData(Importance.Important, AttentionKind.Review, AttentionAlert.Interrupt)]
    [InlineData(Importance.Important, AttentionKind.Error, AttentionAlert.Interrupt)]
    [InlineData(Importance.Important, AttentionKind.Permission, AttentionAlert.Interrupt)]
    [InlineData(Importance.Important, AttentionKind.Question, AttentionAlert.Interrupt)]
    [InlineData(Importance.Important, AttentionKind.Escalation, AttentionAlert.Interrupt)]
    public void AlertOf_FollowsTheTier_ButAQuietSessionsBlockingItemsNotify(Importance importance, AttentionKind kind, AttentionAlert alert) =>
        Assert.Equal(alert, Attention.AlertOf(importance, kind));

    [Fact]
    public void EveryKind_HasAnAlertInEveryTier() =>
        Assert.All(Enum.GetValues<Importance>().SelectMany(i => Enum.GetValues<AttentionKind>().Select(k => (i, k))),
            pair => Assert.True(Enum.IsDefined(Attention.AlertOf(pair.i, pair.k))));

    /// <summary>
    /// Normal and Notify are what every item was before tiers: the JSON leaves them out, and an item without them, from a
    /// server before tiers, reads as them. Quiet and Inbox are written.
    /// </summary>
    [Fact]
    public void TheDefaults_AreLeftOutOfTheJson_AndReadBackAsNormal()
    {
        var normal = new AttentionItem("p/r/s", "s", "p", "r", AttentionKind.Finished, DateTime.UtcNow, "done");
        var json = JsonSerializer.Serialize(normal, JsonDefaults.Options);
        Assert.DoesNotContain("Importance", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Alert", json, StringComparison.OrdinalIgnoreCase);
        var read = JsonSerializer.Deserialize<AttentionItem>(json, JsonDefaults.Options)!;
        Assert.Equal((Importance.Normal, AttentionAlert.Notify), (read.Importance, read.Alert));

        var quiet = JsonSerializer.Serialize(normal with { Importance = Importance.Quiet, Alert = AttentionAlert.Inbox }, JsonDefaults.Options);
        Assert.Contains("\"Quiet\"", quiet);
        Assert.Contains("\"Inbox\"", quiet);
    }

    // ── Sessions ──

    [Fact]
    public async Task ASession_IsNormal_UnlessItsActionSaysOtherwise()
    {
        await using var harness = new LifecycleHarness(Finishing("done"));
        var session = await harness.CreateProjectAsync("session");
        await WaitForResultAsync(harness, session.Id, "done");

        Assert.Equal(Importance.Normal, harness.Tracked(session.Id).Status.Importance);
        Assert.Equal(Importance.Normal, Settings(harness, session.Id).Importance);
        var item = Assert.Single(harness.Projects.GetAttention());
        Assert.Equal((Importance.Normal, AttentionAlert.Notify), (item.Importance, item.Alert));
    }

    /// <summary>A quiet session's result stays in the user's list: the tier says how loud it is, never whether it is there.</summary>
    [Fact]
    public async Task AQuietActionsSession_KeepsItsFinishedInTheList_InTheInboxAlone()
    {
        await using var harness = new LifecycleHarness(Finishing("done"),
            rootConfig: new Dictionary<string, object> { ["importance"] = "quiet" });
        var session = await harness.CreateProjectAsync("session");
        await WaitForResultAsync(harness, session.Id, "done");

        Assert.Equal(Importance.Quiet, Settings(harness, session.Id).Importance);
        var item = Assert.Single(harness.Projects.GetAttention());
        Assert.Equal((session.Id, AttentionKind.Finished, Importance.Quiet, AttentionAlert.Inbox),
            (item.ProjectId, item.Kind, item.Importance, item.Alert));
        Assert.Equal(AttentionAlert.Inbox, Assert.Single(harness.Hub.AttentionPushes[^1]).Alert);
    }

    [Fact]
    public async Task AQuietSessionsQuestion_ReachesTheUser_AndNotifies()
    {
        const string question = "Which branch should I push to?";
        await using var harness = new LifecycleHarness(
            new FakeScript().EmitInit().AwaitStdin().EmitAssistant(question).Sleep(50).EmitResult(question).AwaitStdin(),
            rootConfig: new Dictionary<string, object> { ["importance"] = "quiet" });
        var session = await harness.CreateProjectAsync("session");
        await harness.WaitForStateAsync(session.Id, ProjectState.WaitingInput);

        var item = Assert.Single(harness.Projects.GetAttention());
        Assert.Equal((AttentionKind.Question, AttentionAlert.Notify), (item.Kind, item.Alert));
    }

    [Fact]
    public async Task SetImportance_KeepsItInSettings_PushesTheStatusAndTheList_AndARestartKeepsIt()
    {
        await using var harness = new LifecycleHarness(Finishing("done"));
        var session = await harness.CreateProjectAsync("session");
        await WaitForResultAsync(harness, session.Id, "done");

        await harness.Projects.SetImportanceAsync(session.Id, Importance.Important);

        Assert.Equal(Importance.Important, Settings(harness, session.Id).Importance);
        await harness.WaitForStatusPushAsync(session.Id, status => status.Importance == Importance.Important);
        var pushed = Assert.Single(harness.Hub.AttentionPushes[^1]);
        Assert.Equal((Importance.Important, AttentionAlert.Interrupt), (pushed.Importance, pushed.Alert));
        Assert.Equal(Importance.Important, (await harness.Projects.ListProjectsAsync()).Single(s => s.Id == session.Id).Importance);

        await harness.RestartAsync(resume: false);

        Assert.Equal(Importance.Important, harness.Tracked(session.Id).Status.Importance);
        Assert.Equal(AttentionAlert.Interrupt, Assert.Single(harness.Projects.GetAttention()).Alert);
    }

    /// <summary>The session's settings.json is its tier, not status.json, which a recovery does not read it from.</summary>
    [Fact]
    public async Task ARecovery_ReadsTheTierFromSettings_NotFromStatus()
    {
        await using var harness = new LifecycleHarness(Finishing("done"));
        var session = await harness.CreateProjectAsync("session");
        await WaitForResultAsync(harness, session.Id, "done");
        await harness.Projects.SetImportanceAsync(session.Id, Importance.Quiet);

        var statePath = harness.StatePath(session.Id);
        (ProjectFiles.ProjectSettings.Load(statePath) with { Importance = Importance.Important }).Save(statePath);
        await harness.RestartAsync(resume: false);

        Assert.Equal(Importance.Important, harness.Tracked(session.Id).Status.Importance);
    }

    /// <summary>A session from before tiers has no importance in its settings.json: it is normal.</summary>
    [Fact]
    public void SettingsWithoutImportance_AreNormal()
    {
        var dir = ServerProcess.CreateWorkDir("importance-settings");
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), """{ "actionName": "issue", "sharedFolder": false }""");
            Assert.Equal(Importance.Normal, ProjectFiles.ProjectSettings.Load(dir).Importance);
        }
        finally
        {
            ServerProcess.DeleteWorkDir(dir);
        }
    }

    /// <summary>A later change to the action's default leaves the sessions there are as they are.</summary>
    [Fact]
    public async Task TheConfigsDefault_AppliesAtTheCreate_NotToSessionsThereAre()
    {
        await using var harness = new LifecycleHarness(Finishing("done"),
            rootConfig: new Dictionary<string, object> { ["importance"] = "important" });
        var session = await harness.CreateProjectAsync("session");
        await WaitForResultAsync(harness, session.Id, "done");
        var config = Path.Combine(harness.RootPath, ".godmode-root", "config.json");
        File.WriteAllText(config, File.ReadAllText(config).Replace("\"important\"", "\"quiet\""));

        await harness.RestartAsync(resume: false);

        Assert.Equal(Importance.Important, harness.Tracked(session.Id).Status.Importance);
    }

    [Fact]
    public async Task AnAdoptedSession_TakesItsActionsImportance()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin(),
            rootConfig: new Dictionary<string, object> { ["importance"] = "important" });
        Directory.CreateDirectory(Path.Combine(harness.RootPath, "existing"));
        await harness.Projects.RecoverProjectsAsync();

        var adopted = await harness.AdoptAsync("existing");

        Assert.Equal(Importance.Important, adopted.Importance);
        Assert.Equal(Importance.Important, Settings(harness, adopted.Id).Importance);
    }

    [Fact]
    public async Task SetImportance_OfAnUnknownProject_OrNoTier_IsRefused()
    {
        await using var harness = new LifecycleHarness(Finishing("done"));
        var session = await harness.CreateProjectAsync("session");
        await WaitForResultAsync(harness, session.Id, "done");

        await Assert.ThrowsAsync<KeyNotFoundException>(() => harness.Projects.SetImportanceAsync("p/r/none", Importance.Quiet));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Projects.SetImportanceAsync(session.Id, (Importance)7));
        Assert.Equal(Importance.Normal, Settings(harness, session.Id).Importance);
    }

    /// <summary>
    /// A settings.json that cannot be read is not replaced with the defaults, which would say the session does not share
    /// its folder: the tier is refused, and the file is left as it was.
    /// </summary>
    [Fact]
    public async Task SetImportance_OfASessionWhoseSettingsCannotBeRead_IsRefused_AndLeavesTheFile()
    {
        await using var harness = new LifecycleHarness(Finishing("done"),
            rootConfig: new Dictionary<string, object> { ["sharedFolder"] = true });
        var session = await harness.CreateProjectAsync("session");
        await WaitForResultAsync(harness, session.Id, "done");
        var path = Path.Combine(harness.StatePath(session.Id), "settings.json");
        File.WriteAllText(path, "{ not json");

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Projects.SetImportanceAsync(session.Id, Importance.Important));

        Assert.Equal("{ not json", File.ReadAllText(path));
        Assert.Equal(Importance.Normal, harness.Tracked(session.Id).Status.Importance);
    }

    // ── Config ──

    private static RootConfig Read(params (string File, string Json)[] files)
    {
        var root = ServerProcess.CreateWorkDir("importancecfg");
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
    public void TheRootsImportance_GoesToEveryAction_AndAnOverlayReplacesIt_InAnyCase()
    {
        var config = Read(
            ("config.json", """{ "importance": "quiet" }"""),
            ("config.chat.json", "{}"),
            ("config.overseer.json", """{ "importance": "Important" }"""));

        Assert.Equal(Importance.Quiet, config.ResolveAction("chat")!.Importance);
        Assert.Equal(Importance.Important, config.ResolveAction("overseer")!.Importance);
        Assert.Equal(Importance.Normal, Read(("config.json", "{}")).ResolveAction(null)!.Importance);
    }

    [Theory]
    [InlineData("loud")]
    [InlineData("1")]
    [InlineData("Quiet, Important")]
    public void AnImportanceThatIsNoTier_IsAConfigError(string value)
    {
        var ex = Assert.Throws<InvalidDataException>(() => Read(("config.json", JsonSerializer.Serialize(new { importance = value }))));
        Assert.Contains("importance", ex.Message);
    }
}
