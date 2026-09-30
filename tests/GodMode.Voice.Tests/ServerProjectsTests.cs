using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Voice.Tests;

/// <summary>One server's projects as voice hears them: its list on connection, and the hub's events around it.</summary>
public sealed class ServerProjectsTests
{
    /// <summary>
    /// What the hub pushes while the list is on its way is made on top of it: a project created then is not wiped by
    /// the list (and, its later changes ignored, unknown until a reconnect), and one deleted then is not brought back.
    /// </summary>
    [Fact]
    public void Events_during_the_listing_are_made_after_the_list()
    {
        List<IReadOnlyList<ProjectSummary>> pushed = [];
        var projects = new HubServers.ServerProjects("local", "local", (_, _, list) => pushed.Add(list));

        projects.BeginListing();
        projects.Created(Status("p/r/created", "created"));
        projects.Deleted("p/r/deleted");
        projects.StatusChanged("p/r/kept", Status("p/r/kept", "kept", ProjectState.Running));
        Assert.Empty(pushed);

        // The list, made before the create and the delete
        projects.EndListing([Summary("p/r/deleted", "deleted"), Summary("p/r/kept", "kept")]);

        var list = Assert.Single(pushed);
        Assert.Equal(["created", "kept"], list.Select(p => p.Name).Order());
        Assert.Equal(ProjectState.Running, list.Single(p => p.Name == "kept").State);

        projects.StatusChanged("p/r/created", Status("p/r/created", "created", ProjectState.Running));
        Assert.Equal(ProjectState.Running, pushed[^1].Single(p => p.Name == "created").State);
    }

    [Fact]
    public void A_listing_that_fails_keeps_what_was_heard_and_makes_the_changes_that_waited()
    {
        List<IReadOnlyList<ProjectSummary>> pushed = [];
        var projects = new HubServers.ServerProjects("local", "local", (_, _, list) => pushed.Add(list));
        projects.Created(Status("p/r/old", "old"));

        projects.BeginListing();
        projects.Created(Status("p/r/new", "new"));
        projects.EndListing(null);

        Assert.Equal(["new", "old"], pushed[^1].Select(p => p.Name).Order());
    }

    [Fact]
    public void A_server_let_go_of_during_the_listing_has_no_projects()
    {
        List<IReadOnlyList<ProjectSummary>> pushed = [];
        var projects = new HubServers.ServerProjects("local", "local", (_, _, list) => pushed.Add(list));

        projects.BeginListing();
        projects.Created(Status("p/r/new", "new"));
        projects.Removed();
        projects.EndListing([Summary("p/r/listed", "listed")]);
        projects.Created(Status("p/r/late", "late"));

        Assert.Empty(Assert.Single(pushed));
    }

    private static ProjectStatus Status(string id, string name, ProjectState state = ProjectState.Idle) =>
        new(id, name, state, DateTime.UtcNow, DateTime.UtcNow, null, new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0);

    private static ProjectSummary Summary(string id, string name) => new(id, name, ProjectState.Idle, DateTime.UtcNow);
}
