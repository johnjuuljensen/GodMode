using VoiceBot.Core.Resources;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// A root's title (issue #434): voice says a root by its title, and takes its name too. Two profiles' roots of one
/// title, as the user's Outbound-Assistant and Mega-Assistant, both titled Assistant, are told apart by the profile.
/// </summary>
public sealed class RootTitleTests
{
    private const string Server = "server-a";
    private static readonly ProjectRef OutboundBackup = new(Server, "Outbound/Outbound-Assistant/261003-chat-backup-a1");
    private static readonly ProjectRef MegaBackup = new(Server, "Mega/Mega-Assistant/261003-chat-backup-b2");

    private static readonly VoicePhrases Danish = new(new SessionLanguages("da-DK"));

    private sealed record Voice(FakeServers Servers, ProjectHandles Handles, AttentionBoard Board, VoiceTools Tools);

    /// <summary>The two Assistants, titled as <paramref name="outboundTitle"/> and <paramref name="megaTitle"/>, each with a chat "backup".</summary>
    private static Voice TwoAssistants(string? outboundTitle = "Assistant", string? megaTitle = "Assistant", string megaProfile = "Mega")
    {
        var servers = new FakeServers(Server);
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var board = new AttentionBoard(servers, handles, projects);
        var tools = new VoiceTools(servers, board, projects, handles, new VoiceConversation());
        // The roots come before the projects, as a connection gives them
        servers.AddTitledRoot(Server, "Outbound-Assistant", outboundTitle, "Outbound", Action("chat"))
            .AddTitledRoot(Server, "Mega-Assistant", megaTitle, megaProfile, Action("chat"));
        servers.AddProject(Server, OutboundBackup.ProjectId, "backup", root: "Outbound-Assistant", kind: "chat", profile: "Outbound");
        servers.AddProject(Server, MegaBackup.ProjectId, "backup", root: "Mega-Assistant", kind: "chat", profile: megaProfile);
        return new Voice(servers, handles, board, tools);
    }

    [Fact]
    public void The_list_says_each_root_by_its_title_under_its_profile()
    {
        var voice = TwoAssistants();

        Assert.Equal(
            "2 projects, in 2 groups by profile and root:\n" +
            "Profile Mega, root Assistant (1 project):\n- chat backup (backup, chat): Idle\n" +
            "Profile Outbound, root Assistant (1 project):\n- chat backup (backup, chat): Idle\n" +
            "Say the count, 2, then each group once, by its profile and root, with its projects by the names given here. " +
            "If you leave any out, say how many and why.",
            voice.Tools.ListProjectsText());
    }

    [Fact]
    public void Each_is_said_with_its_title_and_its_profile_and_found_so()
    {
        var voice = TwoAssistants();
        var names = voice.Tools.Names;

        // Another profile has an Assistant too: the profile is said every time
        Assert.Equal("chat backup i Assistant, profil Mega", Danish.Named(names.Of(MegaBackup)!));
        Assert.Equal("chat backup i Assistant, profil Mega", Danish.Named(names.Of(MegaBackup)!));
        Assert.Equal("chat backup i Assistant, profil Outbound", Danish.Named(names.Of(OutboundBackup)!));

        Assert.Equal(MegaBackup, voice.Handles.Resolve("chat backup i Assistant, profil Mega"));
        Assert.Equal(OutboundBackup, voice.Handles.Resolve("backup i Assistant, profil Outbound"));
        // The title alone names both: none
        Assert.Null(voice.Handles.Resolve("chat backup i Assistant"));
        // Its name is taken too
        Assert.Equal(MegaBackup, voice.Handles.Resolve("chat backup i Mega-Assistant"));
        Assert.Equal(OutboundBackup, voice.Handles.Resolve("Outbound-Assistant"));
    }

    [Fact]
    public void Two_roots_of_one_profile_shown_as_one_title_are_said_by_their_names()
    {
        var voice = TwoAssistants(megaProfile: "Outbound");
        var names = voice.Tools.Names;

        Assert.Equal("chat backup i Mega-Assistant", Danish.Named(names.Of(MegaBackup)!));
        Assert.Equal("chat backup i Outbound-Assistant", Danish.Named(names.Of(OutboundBackup)!));
    }

    [Fact]
    public void A_root_with_no_title_is_said_by_its_name_and_a_title_given_later_is_said_from_then()
    {
        var voice = TwoAssistants(outboundTitle: null, megaTitle: null);
        Assert.Equal("chat backup i Mega-Assistant, profil Mega", Danish.Named(voice.Tools.Names.Of(MegaBackup)!));

        // The roots change on the server (RootsChanged): its title is said from now on, and taken
        voice.Servers.Retitle(Server, "Mega-Assistant", "Assistant");
        // No other profile's root is shown as Assistant, and Mega was spoken of last: no profile
        Assert.Equal("chat backup i Assistant", Danish.Named(voice.Tools.Names.Of(MegaBackup)!));
        Assert.Equal(MegaBackup, voice.Handles.Resolve("backup i Assistant, profil Mega"));
    }

    [Fact]
    public async Task A_create_takes_the_title_or_the_name_and_reads_back_the_title()
    {
        var voice = TwoAssistants();
        var tools = voice.Tools;

        Assert.Equal("Ambiguous: 2 roots fit. Ask the user which, as a closed question: Assistant (root Outbound-Assistant, profile Outbound), " +
            "Assistant (root Mega-Assistant, profile Mega). Nothing was created.",
            await tools.StartSessionAsync(new CreateAsk("Assistant", null, null, "notes", null), CancellationToken.None));
        Assert.Null(tools.Creates.TakeProposed());

        Assert.Contains("in Assistant (root Mega-Assistant, profile Mega), action chat",
            await tools.StartSessionAsync(new CreateAsk("Assistant Mega", null, null, "notes", null), CancellationToken.None));
        var request = tools.Creates.TakeProposed()!;
        Assert.Equal("Mega-Assistant", request.Root.Root.Name);
        Assert.Equal("Skal jeg oprette notes uden beskrivelse i Assistant, profil Mega, som chat?", Danish.ReadBack(request));

        Assert.Contains("in Assistant (root Outbound-Assistant, profile Outbound), action chat",
            await tools.StartSessionAsync(new CreateAsk("Outbound-Assistant", null, null, "notes", null), CancellationToken.None));
        Assert.Equal("Outbound-Assistant", tools.Creates.TakeProposed()!.Root.Root.Name);
    }
}
