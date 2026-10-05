using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Voice;

/// <summary>What a listed project is doing, as a long list says it (#457), in the order it says them.</summary>
public enum ListedState { NeedsYou, Running, Idle, Stopped }

/// <summary>
/// A project as a list says it: what it is doing, its profile and root (as shown), its label, and its server's name when
/// its group is told apart by it (<see cref="ProjectNames.Group.Server"/>).
/// </summary>
public sealed record ListedProject(ListedState State, string Profile, string? Root, ServerProject Project, string Label, string? Server = null);

/// <summary>A group of a list said whole (<see cref="VoicePhrases.Projects"/>): its profile and root, its projects as said, and its server when it needs it.</summary>
public sealed record ListedGroup(string Profile, string? Root, IReadOnlyList<string> Labels, string? Server = null);

/// <summary>A state as a long list's summary says it: its count, and the projects it names, none when it only counts them.</summary>
public sealed record StateCount(ListedState State, int Count, IReadOnlyList<ListedProject> Named);

/// <summary>
/// How a long project list is said (#457): its projects in one order, by state (<see cref="ListedState"/>), then the
/// most recent first (#468); a summary that names the states few enough to name and counts the rest; then the projects
/// it did not name, in pages of <see cref="Page"/>. A list of at most <see cref="Page"/> is said whole, by profile and
/// root (<see cref="VoicePhrases.Projects"/>). What a list takes in is decided here alone (<see cref="Within"/>): stale
/// sessions, or those outside a window the user asked for, are left out and counted, and what else leaves projects
/// out of a list goes there too.
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

    /// <summary>
    /// When the project last did something (#468): now while it runs; else the last line of its main conversation, its
    /// last turn's end or its last recap, whichever came last; else, for one that has done none of them since it was
    /// recorded, when its status last changed.
    /// </summary>
    public static DateTime ActivityOf(ProjectSummary project, DateTime now) =>
        project.State == ProjectState.Running ? now
        : new[] { project.LastOutputAt, project.LastResultAt, project.RecapAt }.Max() ?? project.UpdatedAt;

    /// <summary>
    /// What a list takes in (#468), the one place that leaves projects out of one: those with activity
    /// (<paramref name="activity"/>) since the window's start, the most recent first, and how many it left out. A project
    /// that needs the user is left out only by a window the user asked for: never as stale. All when the window has no start.
    /// </summary>
    public static (IReadOnlyList<T> Kept, LeftOut LeftOut) Within<T>(IEnumerable<T> projects, ListWindow window, Func<T, DateTime> activity, Func<T, bool> needsUser)
    {
        var all = projects.Select(p => (Project: p, At: activity(p))).OrderByDescending(p => p.At).ToList();
        List<T> kept = [.. all.Where(p => window.Since is not { } since || p.At >= since || !window.Asked && needsUser(p.Project)).Select(p => p.Project)];
        var left = all.Select(p => p.Project).Except(kept).ToList();
        return (kept, new LeftOut(left.Count, window.Asked, left.Count(needsUser)));
    }

    /// <summary>
    /// The groups' projects in the order a long list says them: by state, then the most recent first
    /// (<paramref name="activity"/>, #468), a state's of one group and root kept with each other only where their times fall so.
    /// </summary>
    public static IReadOnlyList<ListedProject> Order(IReadOnlyList<ProjectNames.Group> groups, Func<ServerProject, ListedState> state,
        Func<ServerProject, string> label, Func<ServerProject, DateTime> activity) =>
        [.. groups.SelectMany(g => g.Projects.Select(p => new ListedProject(state(p), g.Profile, g.Root, p, label(p), g.Server)))
            .OrderBy(p => p.State).ThenByDescending(p => activity(p.Project))];

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

    /// <summary>
    /// The projects in pages of <see cref="Page"/>, in order: each page the next most recent, its projects of one state,
    /// profile and root said together, so a page names each group once.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<ListedProject>> Pages(IReadOnlyList<ListedProject> rest) =>
        [.. rest.Chunk(Page).Select(page => (IReadOnlyList<ListedProject>)[.. page.GroupBy(p => (p.State, p.Profile, p.Root, p.Server)).SelectMany(g => g)])];
}

/// <summary>
/// What a list takes in (#468): the projects with activity since <paramref name="Since"/>, all when null.
/// <paramref name="Asked"/> when the user asked for the window ("den sidste time", "siden jeg sidst spurgte"); else it is
/// the default, which leaves out stale projects, and never one that needs the user.
/// </summary>
public sealed record ListWindow(DateTime? Since, bool Asked)
{
    /// <summary>Every project: "alle", "også de gamle".</summary>
    public static readonly ListWindow All = new(null, false);

    /// <summary>The default: what has been active within <paramref name="staleAfter"/>, and what needs the user.</summary>
    public static ListWindow Recent(DateTime now, TimeSpan staleAfter) => new(now - staleAfter, false);

    /// <summary>What the user asked for: the projects with activity since <paramref name="since"/>.</summary>
    public static ListWindow After(DateTime since) => new(since, true);
}

/// <summary>
/// How many projects a list left out (#468): stale ones, or, <paramref name="Asked"/>, those with nothing new in the window
/// asked for, of which <paramref name="Waiting"/> need the user from before (#507), which only a window asked for leaves out.
/// </summary>
public sealed record LeftOut(int Count, bool Asked, int Waiting = 0);
