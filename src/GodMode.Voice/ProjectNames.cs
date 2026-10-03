namespace GodMode.Voice;

/// <summary>
/// A project as it is named aloud (#450): its label (<see cref="ProjectHandles.Label(string, string, string?)"/>,
/// "issue 376", "branch master"), and the root and profile it is in, those it is said with. <see cref="ToString"/> is
/// the name in a tool's result, for the model; <see cref="VoicePhrases"/> says it in the user's language.
/// </summary>
public sealed record SpokenName(string Label, string? Root = null, string? Profile = null)
{
    /// <summary>"issue 376 in GodMode, profile Mega".</summary>
    public override string ToString() =>
        Label + (Root is { } root ? $" in {root}" : "") + (Profile is { } profile ? $", profile {profile}" : "");
}

/// <summary>
/// How voice names projects, so the user can tell which is which across profiles and roots (#450). A project named
/// alone is said with its root, when there are projects in more than one root, and with its profile, when there are
/// several and another profile has a root of its root's name, or it is not in the profile spoken of last
/// (<see cref="VoiceConversation.LastProfile"/>). Lists say each profile and root once, as a group's heading
/// (<see cref="Groups"/>).
/// </summary>
public sealed class ProjectNames(ProjectBoard projects, ProjectHandles handles, VoiceConversation conversation)
{
    /// <summary>A profile and root, and their projects, the one changed last first.</summary>
    public sealed record Group(string Profile, string? Root, IReadOnlyList<ServerProject> Projects)
    {
        /// <summary>"Profile Mega, root GodMode".</summary>
        public string Heading => $"Profile {Profile}, root {Root ?? "none"}";
    }

    /// <summary>
    /// The project named alone, as it is said now: with its root and profile as the class says. It is spoken of
    /// from now on: its profile is the last one. Null when it has no handle.
    /// </summary>
    public SpokenName? Of(ProjectRef project)
    {
        if (handles.LabelOf(project) is not { } label)
            return null;
        if (projects.Find(project)?.Project is not { } summary)
            return new SpokenName(label);

        var all = projects.Projects;
        var profile = ProfileOf(summary);
        var said = SeveralProfiles(all) && (SameRootElsewhere(all, profile, summary.RootName) || !Same(conversation.LastProfile, profile));
        conversation.LastProfile = profile;
        return new SpokenName(label, SeveralRoots(all) ? summary.RootName : null, said ? profile : null);
    }

    /// <summary>
    /// The project with its root and profile whenever there are several, as a list of options names it: it does not
    /// change what was spoken of last.
    /// </summary>
    public SpokenName? Full(ProjectRef project)
    {
        if (handles.LabelOf(project) is not { } label)
            return null;
        if (projects.Find(project)?.Project is not { } summary)
            return new SpokenName(label);
        var all = projects.Projects;
        return new SpokenName(label, SeveralRoots(all) ? summary.RootName : null, SeveralProfiles(all) ? ProfileOf(summary) : null);
    }

    /// <summary>
    /// Every project, grouped by profile, then root: the profiles in their names' order, the roots in each too, and the
    /// projects in each the one changed last first. A list that names several profiles leaves none the last spoken of;
    /// one of one profile leaves it.
    /// </summary>
    public IReadOnlyList<Group> Groups()
    {
        var groups = projects.Projects
            .GroupBy(p => (Profile: ProfileOf(p.Project), Root: p.Project.RootName ?? ""), Comparer)
            .OrderBy(g => g.Key.Profile, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Key.Root, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Group(g.Key.Profile, g.Key.Root.Length > 0 ? g.Key.Root : null, [.. g]))
            .ToList();
        conversation.LastProfile = groups.Select(g => g.Profile).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? groups[0].Profile : null;
        return groups;
    }

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

    /// <summary>Whether a project in another profile is in a root of this name.</summary>
    private static bool SameRootElsewhere(IReadOnlyList<ServerProject> all, string profile, string? root) =>
        root is not null && all.Any(p => Same(p.Project.RootName, root) && !Same(ProfileOf(p.Project), profile));

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
