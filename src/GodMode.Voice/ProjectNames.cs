namespace GodMode.Voice;

/// <summary>
/// A project as it is named aloud (#450): its label (<see cref="ProjectHandles.Label(string, string, string?)"/>,
/// "issue 376", "branch master"), its spoken topic (<see cref="ProjectTopics"/>, #455), and the root and profile it is in,
/// those it is said with. <see cref="ToString"/> is the name in a tool's result, for the model; <see cref="VoicePhrases"/>
/// says it in the user's language.
/// </summary>
public sealed record SpokenName(string Label, string? Root = null, string? Profile = null, string? Topic = null)
{
    /// <summary>"issue 376, mic-timeout in GodMode, profile Mega".</summary>
    public override string ToString() =>
        Label + (Topic is { } topic ? $", {topic}" : "") + (Root is { } root ? $" in {root}" : "") + (Profile is { } profile ? $", profile {profile}" : "");
}

/// <summary>
/// How voice names projects, so the user can tell which is which across profiles and roots (#450), and can place a line
/// about one whatever they had in mind before it (#455): brief in words, never in context. How much a line says of the
/// project it names is its anchor (<see cref="VoiceConversation.Mention"/>), by what the lines before it were about: the
/// label alone for the project the last line was about; its label and topic for another, with its root when that is
/// not the root spoken of last (<see cref="VoiceConversation.LastRoot"/>) or another project has its label, and its
/// profile when another profile has a root shown as its root is, or it is not in the profile spoken of last
/// (<see cref="VoiceConversation.LastProfile"/>); and all of it, its root and profile whenever there are several, for a
/// project not named in a while. A root is said as it is shown, by its title (#434, <see cref="ProjectBoard.RootShown"/>),
/// so two profiles' roots of one title are told apart by their profiles. Lists say each profile and root once, as a
/// group's heading (<see cref="Groups"/>).
/// </summary>
public sealed class ProjectNames(ProjectBoard projects, ProjectHandles handles, VoiceConversation conversation)
{
    /// <summary>
    /// A profile and root on one server, and their projects, the one changed last first. <paramref name="Server"/> is the
    /// server's name when another server has a profile and root said as these are (#507), which it tells them apart by;
    /// null otherwise.
    /// </summary>
    public sealed record Group(string Profile, string? Root, IReadOnlyList<ServerProject> Projects, string? Server = null)
    {
        /// <summary>"Profile Mega, root GodMode", and ", server work-pc" when it needs it.</summary>
        public string Heading => $"Profile {Profile}, root {Root ?? "none"}{(Server is { } server ? $", server {server}" : "")}";
    }

    /// <summary>
    /// The project named alone, in a line said now: anchored as the class says. It is mentioned from now on: the
    /// project, its root and its profile are the last ones. Null when it has no handle.
    /// </summary>
    public SpokenName? Of(ProjectRef project)
    {
        if (handles.LabelOf(project) is not { } label)
            return null;
        var anchor = conversation.Mention(project);
        if (projects.Find(project) is not { } found)
            return new SpokenName(label);

        var all = projects.Projects;
        var profile = ProfileOf(found.Project);
        var root = projects.RootShown(found);
        var (saysRoot, saysProfile) = anchor switch
        {
            Anchor.Bare => (false, false),
            Anchor.Full => (SeveralRoots(all), SeveralProfiles(all)),
            _ => (SeveralRoots(all) && (!Same(conversation.LastRoot, root) || handles.Labelled(label).Count > 1),
                SeveralProfiles(all) && (SameRootElsewhere(all, profile, root) || !Same(conversation.LastProfile, profile))),
        };
        conversation.LastProfile = profile;
        conversation.LastRoot = root;
        return new SpokenName(label, saysRoot ? root : null, saysProfile && !OneName(all, saysRoot, profile, root) ? profile : null,
            anchor == Anchor.Bare ? null : TopicOf(found, label));
    }

    /// <summary>
    /// The project with its topic, and its root and profile whenever there are several, as a list of options names it:
    /// it is no line about it, and changes nothing of what was spoken of last.
    /// </summary>
    public SpokenName? Full(ProjectRef project)
    {
        if (handles.LabelOf(project) is not { } label)
            return null;
        if (projects.Find(project) is not { } found)
            return new SpokenName(label);
        var all = projects.Projects;
        var (root, profile, saysRoot) = (projects.RootShown(found), ProfileOf(found.Project), SeveralRoots(all));
        return new SpokenName(label, saysRoot ? root : null, SeveralProfiles(all) && !OneName(all, saysRoot, profile, root) ? profile : null,
            TopicOf(found, label));
    }

    /// <summary>
    /// Whether a root said is the profile's name too (#529: "i GodMode, profil Godmode"), and no other profile has a root
    /// shown so: the root said names both, once.
    /// </summary>
    private bool OneName(IReadOnlyList<ServerProject> all, bool saysRoot, string profile, string? root) =>
        saysRoot && Same(root, profile) && !SameRootElsewhere(all, profile, root);

    /// <summary>The project's spoken topic (<see cref="ProjectTopics"/>); null when its name gives none.</summary>
    private static string? TopicOf(ServerProject project, string label) => ProjectTopics.Of(project.Project.Name, project.Project.Kind, label);

    /// <summary>
    /// Every project, grouped by profile, then root, on its server: the profiles in their names' order, the roots in each
    /// too, and the projects in each the one changed last first. Two servers' groups said alike are told apart by their
    /// servers' names (<see cref="Group.Server"/>, #507). A list that names several profiles leaves none the last spoken of;
    /// one of one profile leaves it, and the same of roots.
    /// </summary>
    public IReadOnlyList<Group> Groups()
    {
        var grouped = projects.Projects
            .GroupBy(p => (p.ServerId, Profile: ProfileOf(p.Project), Root: p.Project.RootName ?? ""), OnServer)
            .Select(g => new Group(g.Key.Profile, projects.RootShown(g.First()), [.. g], g.First().ServerName))
            .ToList();
        var groups = grouped
            .Select(g => g with { Server = grouped.Where(o => Same(o.Profile, g.Profile) && Same(o.Root, g.Root))
                .Select(o => o.Projects[0].ServerId).Distinct().Skip(1).Any() ? g.Server : null })
            .OrderBy(g => g.Profile, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Root ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Projects[0].ServerName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        conversation.LastProfile = groups.Select(g => g.Profile).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? groups[0].Profile : null;
        conversation.LastRoot = groups is [var one] ? one.Root : null;
        return groups;
    }

    private static readonly IEqualityComparer<(string ServerId, string Profile, string Root)> OnServer = EqualityComparer<(string, string, string)>.Create(
        (a, b) => a.Item1 == b.Item1 && Same(a.Item2, b.Item2) && Same(a.Item3, b.Item3),
        k => HashCode.Combine(k.Item1, k.Item2.ToUpperInvariant(), k.Item3.ToUpperInvariant()));

    private static readonly IEqualityComparer<(string Profile, string Root)> Comparer = EqualityComparer<(string, string)>.Create(
        (a, b) => Same(a.Item1, b.Item1) && Same(a.Item2, b.Item2),
        k => HashCode.Combine(k.Item1.ToUpperInvariant(), k.Item2.ToUpperInvariant()));

    /// <summary>The project's profile, as the app names it: <c>Default</c> when it has none.</summary>
    private static string ProfileOf(GodMode.Shared.Models.ProjectSummary project) => project.ProfileName ?? "Default";

    private static bool SeveralProfiles(IReadOnlyList<ServerProject> all) =>
        all.Select(p => ProfileOf(p.Project)).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any();

    /// <summary>Whether the projects are in more than one root (a profile and root): one with none is in none.</summary>
    private static bool SeveralRoots(IReadOnlyList<ServerProject> all) =>
        all.Where(p => p.Project.RootName is not null).Select(p => (ProfileOf(p.Project), p.Project.RootName!)).Distinct(Comparer).Skip(1).Any();

    /// <summary>Whether a project in another profile is in a root shown as this one is.</summary>
    private bool SameRootElsewhere(IReadOnlyList<ServerProject> all, string profile, string? root) =>
        root is not null && all.Any(p => Same(projects.RootShown(p), root) && !Same(ProfileOf(p.Project), profile));

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
