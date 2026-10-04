using GodMode.Shared.Enums;

namespace GodMode.Voice;

/// <summary>What a listed project is doing, as a long list says it (#457), in the order it says them.</summary>
public enum ListedState { NeedsYou, Running, Idle, Stopped }

/// <summary>A project as a list says it: what it is doing, its profile and root (as shown), and its label.</summary>
public sealed record ListedProject(ListedState State, string Profile, string? Root, ServerProject Project, string Label);

/// <summary>A state as a long list's summary says it: its count, and the projects it names, none when it only counts them.</summary>
public sealed record StateCount(ListedState State, int Count, IReadOnlyList<ListedProject> Named);

/// <summary>
/// How a long project list is said (#457): its projects in one order, by state (<see cref="ListedState"/>), then by
/// profile and root as <see cref="ProjectNames.Groups"/> has them; a summary that names the states few enough to name
/// and counts the rest; then the projects it did not name, in pages of <see cref="Page"/>. A list of at most
/// <see cref="Page"/> is said whole, by profile and root (<see cref="VoicePhrases.Projects"/>). The order is here alone,
/// so what sorts the lists further (recency, stale sessions left out) changes <see cref="Order"/> and nothing else.
/// </summary>
public static class ProjectListing
{
    /// <summary>The most projects said in one go, by their labels, before "Mere?": what a listener keeps.</summary>
    public const int Page = 5;

    /// <summary>What the project is doing: needs the user (it has an attention item, or waits, or failed), runs, idles or is stopped.</summary>
    public static ListedState StateOf(ProjectState state, bool needsUser) =>
        needsUser ? ListedState.NeedsYou
        : state switch
        {
            ProjectState.WaitingInput or ProjectState.WaitingPermission or ProjectState.Error => ListedState.NeedsYou,
            ProjectState.Running => ListedState.Running,
            ProjectState.Stopped => ListedState.Stopped,
            _ => ListedState.Idle,
        };

    /// <summary>The groups' projects in the order a long list says them: by state, then each group's, in the groups' order.</summary>
    public static IReadOnlyList<ListedProject> Order(IReadOnlyList<ProjectNames.Group> groups, Func<ServerProject, ListedState> state, Func<ServerProject, string> label) =>
        [.. groups.SelectMany(g => g.Projects.Select(p => new ListedProject(state(p), g.Profile, g.Root, p, label(p)))).OrderBy(p => p.State)];

    /// <summary>
    /// The summary of <paramref name="listed"/>: each state with projects, in order, named while its projects fit in what
    /// is left of a <see cref="Page"/>, else counted; and the projects it leaves for the pages, in order.
    /// </summary>
    public static (IReadOnlyList<StateCount> States, IReadOnlyList<ListedProject> Left) Summarise(IReadOnlyList<ListedProject> listed)
    {
        var left = Page;
        List<StateCount> states = [];
        foreach (var state in listed.GroupBy(p => p.State).OrderBy(g => g.Key))
        {
            var projects = state.ToList();
            var named = projects.Count <= left;
            if (named)
                left -= projects.Count;
            states.Add(new StateCount(state.Key, projects.Count, named ? projects : []));
        }
        var said = states.SelectMany(s => s.Named).ToHashSet();
        return (states, [.. listed.Where(p => !said.Contains(p))]);
    }

    /// <summary>The projects in pages of <see cref="Page"/>, in order.</summary>
    public static IReadOnlyList<IReadOnlyList<ListedProject>> Pages(IReadOnlyList<ListedProject> rest) => [.. rest.Chunk(Page)];
}
