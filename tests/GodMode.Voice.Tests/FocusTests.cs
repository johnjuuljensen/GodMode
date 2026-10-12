using GodMode.Shared.Models;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// The focus (#287): "Skift til Kappe" / "switch to api" puts the conversation on a profile or a root, as "skift til
/// issue 283" puts it on a project, and the commands that take a root or profile use it when the user names none, saying
/// so ("I profil Kappe: …") for a user with no screen. A command that names its own uses that. "Skift til alle" clears it,
/// and "tilbage" goes back to the focus before. Announcements keep covering everything.
/// </summary>
public sealed class FocusTests
{
    private const string ServerA = "server-a";
    private const string Id283 = "Mega/GodMode/261009-issue-283-mic-k7q2";
    private const string Id12 = "Kappe/api/261009-issue-12-auth-m3x9";
    private const string Id14 = "Kappe/web/261009-issue-14-css-p4z1";

    private static FakeServers Servers()
    {
        var servers = new FakeServers(ServerA);
        servers.AddRoot(ServerA, "GodMode", "Mega", Action("issue", IssueSchema));
        servers.AddRoot(ServerA, "api", "Kappe", Action("issue", IssueSchema));
        servers.AddRoot(ServerA, "web", "Kappe", Action("issue", IssueSchema));
        servers.AddProject(ServerA, Id283, "283-mic", root: "GodMode", kind: "issue", profile: "Mega");
        servers.AddProject(ServerA, Id12, "12-auth", root: "api", kind: "issue", profile: "Kappe");
        servers.AddProject(ServerA, Id14, "14-css", root: "web", kind: "issue", profile: "Kappe");
        servers.Set(ServerA, Question(Id283, "283-mic", "Which mic?", minutesAgo: 2), Question(Id12, "12-auth", "Which token?", minutesAgo: 1));
        return servers;
    }

    private static Dictionary<string, object?> Root(string root) => new() { [VoiceTools.RootParameter] = root };

    private static Task SaysAsync(OfflineVoice voice, Func<string, bool> said, string what) =>
        Eventually.UntilAsync(() => voice.Events.Responses.Any(said), () => $"the bot to say {what}; it said: {string.Join(" | ", voice.Events.Responses)}");

    private static async Task<OfflineVoice> FocusedAsync(ScriptedChatClient model, string root = "Kappe", string said = "Fokus på profil Kappe.")
    {
        var voice = await OfflineVoice.StartAsync(Servers(), model);
        await voice.Events.SaidAsync("Klar.");
        voice.Transcriptions.SayAsRecognized($"Skift til {root}.");
        await voice.Events.SaidAsync(said);
        return voice;
    }

    [Fact]
    public async Task A_profile_focus_is_what_needs_me_when_the_user_names_none_and_the_reply_says_so()
    {
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.SwitchFocus, Root("Kappe"))
            .CallTool(VoiceTools.WhatNeedsMe);
        await using var voice = await FocusedAsync(model);

        voice.Transcriptions.SayAsRecognized("Hvad venter?");
        await SaysAsync(voice, r => r.StartsWith("I profil Kappe:", StringComparison.Ordinal), "what needs me in Kappe, saying so");

        var said = voice.Events.Responses.Last();
        Assert.Contains("issue 12", said);
        Assert.DoesNotContain("283", said);
    }

    [Fact]
    public async Task A_root_focus_is_the_list_when_the_user_names_none()
    {
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.SwitchFocus, Root("api"))
            .CallTool(VoiceTools.ListProjects);
        await using var voice = await FocusedAsync(model, "api", "Fokus på api, profil Kappe.");

        voice.Transcriptions.SayAsRecognized("Hvilke projekter er der?");
        await SaysAsync(voice, r => r.StartsWith("I api", StringComparison.Ordinal), "the projects of api, saying so");

        var said = voice.Events.Responses.Last();
        Assert.Contains("issue 12", said);
        Assert.DoesNotContain("issue 14", said);
    }

    /// <summary>A command that names its own root or profile uses that, whatever the focus, and says no focus.</summary>
    [Fact]
    public async Task A_command_that_names_its_own_scope_overrides_the_focus()
    {
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.SwitchFocus, Root("Kappe"))
            .CallTool(VoiceTools.WhatNeedsMe, Root("Mega"));
        await using var voice = await FocusedAsync(model);

        voice.Transcriptions.SayAsRecognized("Hvad venter i Mega?");
        await SaysAsync(voice, r => r.Contains("283", StringComparison.Ordinal), "what needs me in Mega");

        var said = voice.Events.Responses.Last();
        Assert.DoesNotContain("Kappe", said);
        Assert.DoesNotContain("issue 12", said);
    }

    /// <summary>A start that names no root starts in the root in focus, which its read-back names.</summary>
    [Fact]
    public async Task A_start_with_no_root_starts_in_the_root_in_focus()
    {
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.SwitchFocus, Root("api"))
            .CallTool(VoiceTools.StartSession, new() { [VoiceTools.ActionParameter] = "issue", [VoiceTools.IssueParameter] = "7" });
        await using var voice = await FocusedAsync(model, "api", "Fokus på api, profil Kappe.");

        voice.Transcriptions.SayAsRecognized("Start issue 7.");
        await SaysAsync(voice, r => r.Contains("issue 7", StringComparison.OrdinalIgnoreCase) && r.EndsWith('?'), "the create read back");

        Assert.Contains("api", voice.Events.Responses.Last());
    }

    /// <summary>"Skift til alle" clears the focus: what needs me covers everything again; "tilbage" goes back to the focus before.</summary>
    [Fact]
    public async Task Everything_clears_the_focus_and_back_returns_to_it()
    {
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.SwitchFocus, Root("Kappe"))
            .CallTool(VoiceTools.SwitchFocus, Root("alle"))
            .CallTool(VoiceTools.WhatNeedsMe)
            .CallTool(VoiceTools.GoBack);
        await using var voice = await FocusedAsync(model);

        voice.Transcriptions.SayAsRecognized("Skift til alle.");
        await voice.Events.SaidAsync("Fokus på alle projekter.");
        voice.Transcriptions.SayAsRecognized("Hvad venter?");
        await SaysAsync(voice, r => r.Contains("283", StringComparison.Ordinal) && r.Contains("12", StringComparison.Ordinal), "what needs me everywhere");
        Assert.DoesNotContain("I profil", voice.Events.Responses.Last());

        voice.Transcriptions.SayAsRecognized("Tilbage.");
        await voice.Events.SaidAsync("Tilbage til profil Kappe.");
    }
}
