using GodMode.ClientBase.Layout;
using GodMode.ClientBase.Services;
using GodMode.ClientBase.Services.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodMode.Relay.Tests;

/// <summary>
/// What a restart of the Windows app does with the windows it had (#341): windows.json round-trips, and a profile, a
/// desktop or a screen that is gone each falls back. Placing the windows on their desktops is the app's, live.
/// </summary>
public sealed class WindowLayoutTests : IDisposable
{
    private static readonly Guid DesktopOne = Guid.Parse("7a1b2c3d-0000-4000-8000-000000000001");
    private static readonly Guid DesktopTwo = Guid.Parse("7a1b2c3d-0000-4000-8000-000000000002");

    /// <summary>A 1920×1080 primary with its taskbar, and a second monitor to its right.</summary>
    private static readonly WindowBounds Primary = new(0, 0, 1920, 1032);
    private static readonly WindowBounds Right = new(1920, 0, 2560, 1392);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"godmode-windows-{Guid.NewGuid():N}");

    public WindowLayoutTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string File => Path.Combine(_dir, WindowLayout.FileName);

    private static readonly WindowLayout Three = new([
        new SavedWindow(null, new WindowBounds(100, 80, 1400, 900), Maximized: false, DesktopOne),
        new SavedWindow("Work", new WindowBounds(-1800, 40, 1600, 1000), Maximized: true, DesktopTwo),
        new SavedWindow("Private", new WindowBounds(2000, 100, 1200, 800), Maximized: false, Desktop: null),
    ]);

    [Fact]
    public void The_saved_file_round_trips_every_window_in_order()
    {
        Three.Save(File);

        var loaded = WindowLayout.Load(File, NullLogger.Instance);

        Assert.NotNull(loaded);
        Assert.Equal(Three.Windows, loaded.Windows);
        Assert.Equal(Three.ToJson(), loaded.ToJson());
        Assert.False(System.IO.File.Exists(File + ".tmp"));
    }

    [Fact]
    public void The_file_names_each_window_by_profile_with_its_bounds_maximised_state_and_desktop()
    {
        Three.Save(File);

        var json = System.IO.File.ReadAllText(File);

        Assert.Contains("\"profile\": \"Work\"", json);
        Assert.Contains("\"maximized\": true", json);
        Assert.Contains($"\"desktop\": \"{DesktopTwo}\"", json);
        Assert.Contains("\"x\": -1800", json);
    }

    [Fact]
    public void The_main_window_is_the_first_with_no_profile_and_a_profile_saved_twice_opens_once()
    {
        var layout = new WindowLayout([
            new SavedWindow("Work", new WindowBounds(0, 0, 800, 600), false, null),
            new SavedWindow(null, new WindowBounds(10, 10, 800, 600), false, null),
            new SavedWindow("work", new WindowBounds(20, 20, 800, 600), false, null),
            new SavedWindow(null, new WindowBounds(30, 30, 800, 600), false, null),
        ]);

        Assert.Equal(10, layout.Main!.Bounds.X);
        Assert.Equal(["Work"], layout.Profiles.Select(p => p.Profile));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[1, 2]")]
    [InlineData("{\"windows\": null}")]
    public void A_missing_or_unreadable_file_is_no_layout(string? content)
    {
        if (content is not null) System.IO.File.WriteAllText(File, content);

        Assert.Null(WindowLayout.Load(File, NullLogger.Instance));
    }

    [Fact]
    public void A_profile_no_server_has_is_not_reopened_once_every_server_answered()
    {
        var census = new ProfileCensus(new HashSet<string>(["Private"], StringComparer.OrdinalIgnoreCase), Complete: true);

        Assert.False(WindowPlacer.Reopens("Work", census));
        Assert.True(WindowPlacer.Reopens("Private", census));
        Assert.True(WindowPlacer.Reopens("private", census));
    }

    [Fact]
    public void A_profile_missing_from_an_incomplete_census_is_reopened_since_a_silent_server_may_have_it()
    {
        var census = new ProfileCensus(new HashSet<string>(["Private"], StringComparer.OrdinalIgnoreCase), Complete: false);

        Assert.True(WindowPlacer.Reopens("Work", census));
    }

    [Fact]
    public void A_desktop_that_is_gone_means_the_current_one()
    {
        var saved = Three.Windows[1];

        Assert.Null(WindowPlacer.Place(saved, desktopExists: _ => false, [Primary]).Desktop);
        Assert.Equal(DesktopTwo, WindowPlacer.Place(saved, desktopExists: d => d == DesktopTwo, [Primary]).Desktop);
    }

    [Fact]
    public void A_window_on_a_screen_that_is_there_keeps_its_bounds_and_state()
    {
        var saved = new SavedWindow("Private", new WindowBounds(2000, 100, 1200, 800), Maximized: true, DesktopOne);

        var place = WindowPlacer.Place(saved, _ => true, [Primary, Right]);

        Assert.Equal(new WindowPlace(saved.Bounds, true, DesktopOne), place);
    }

    [Fact]
    public void A_window_on_a_monitor_that_was_removed_comes_back_centred_on_the_primary()
    {
        var saved = new SavedWindow("Private", new WindowBounds(2000, 100, 1200, 800), false, null);

        var place = WindowPlacer.Place(saved, _ => true, [Primary]);

        Assert.Equal(new WindowBounds(360, 116, 1200, 800), place.Bounds);
    }

    [Fact]
    public void A_window_bigger_than_the_screen_it_is_brought_to_is_shrunk_to_fit()
    {
        var bounds = WindowPlacer.OnScreen(new WindowBounds(-3000, 0, 2560, 1392), [Primary]);

        Assert.Equal(Primary, bounds);
    }

    [Theory]
    [InlineData(1800, 500)]  // mostly off the right edge, 120 px of its title bar showing
    [InlineData(-900, 0)]    // half off the left edge
    [InlineData(100, 1010)]  // its title bar just above the taskbar
    public void A_window_whose_title_bar_a_screen_shows_stays_where_it_is(int x, int y)
    {
        var bounds = new WindowBounds(x, y, 1200, 800);

        Assert.Equal(bounds, WindowPlacer.OnScreen(bounds, [Primary]));
    }

    [Theory]
    [InlineData(1850, 500)]  // 70 px of its title bar showing: too little to take hold of
    [InlineData(100, -300)]  // its title bar above the top of every screen
    [InlineData(100, 1020)]  // its title bar behind the taskbar
    public void A_window_whose_title_bar_no_screen_shows_is_brought_back(int x, int y) =>
        Assert.NotEqual(new WindowBounds(x, y, 1200, 800), WindowPlacer.OnScreen(new WindowBounds(x, y, 1200, 800), [Primary]));

    [Fact]
    public void With_no_screens_known_the_bounds_stay() =>
        Assert.Equal(Three.Windows[1].Bounds, WindowPlacer.OnScreen(Three.Windows[1].Bounds, []));
}

/// <summary>The census that decides a profile no longer exists: every server's ListProfiles, over direct connections.</summary>
public sealed class ProfileCensusTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"godmode-census-{Guid.NewGuid():N}");
    private ServerRegistryService _registry = null!;
    private FlakyDirectory _directory = null!;
    private FakeAttentionServer _alpha = null!;
    private FakeAttentionServer _beta = null!;

    public async Task InitializeAsync()
    {
        _alpha = await FakeAttentionServer.StartAsync();
        _beta = await FakeAttentionServer.StartAsync();
        _registry = new ServerRegistryService(_dataDir, new InMemorySecretStore());
        _directory = new FlakyDirectory(new ServerDirectory(_registry, new ServerUrlSelector(ServerUrlSelector.CreateHttpClient()), NullLoggerFactory.Instance));
    }

    public async Task DisposeAsync()
    {
        await _alpha.DisposeAsync();
        await _beta.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<string> AddAsync(string url) =>
        (await _registry.AddServerAsync(new ServerRegistration { Type = ServerTypes.Local, Urls = [url] }, "key")).Id;

    private Task<ProfileCensus> TakeAsync(TimeSpan? wait = null) =>
        ProfileCensus.TakeAsync(_directory, NullLoggerFactory.Instance, wait ?? TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(50));

    [Fact]
    public async Task Every_server_answering_is_a_complete_census_of_their_profiles()
    {
        _alpha.SetProfiles("Default", "Work");
        _beta.SetProfiles("work", "Private");
        await AddAsync(_alpha.Url);
        await AddAsync(_beta.Url);

        var census = await TakeAsync();

        Assert.True(census.Complete);
        Assert.Equal(["Default", "Private", "Work"], census.Names.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Contains("WORK", census.Names);
    }

    [Fact]
    public async Task A_server_that_does_not_answer_leaves_it_incomplete_with_what_the_others_have()
    {
        _alpha.SetProfiles("Work");
        await AddAsync(_alpha.Url);
        await AddAsync(Net.UnreachableUrl());

        var census = await TakeAsync(TimeSpan.FromSeconds(1));

        Assert.False(census.Complete);
        Assert.Equal(["Work"], census.Names);
    }

    [Fact]
    public async Task A_registration_that_cannot_be_listed_leaves_it_incomplete()
    {
        _alpha.SetProfiles("Work");
        var alpha = await AddAsync(_alpha.Url);
        _directory.FailOnce(alpha);

        var census = await TakeAsync();

        Assert.False(census.Complete);
    }

    [Fact]
    public async Task No_servers_at_all_is_a_complete_census_of_no_profiles()
    {
        var census = await TakeAsync();

        Assert.True(census.Complete);
        Assert.Empty(census.Names);
    }
}
