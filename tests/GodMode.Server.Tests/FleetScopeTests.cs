using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using ModelContextProtocol.Client;
using static GodMode.Server.Tests.FleetRun;

namespace GodMode.Server.Tests;

/// <summary>
/// A session's fleet tools see its own profile alone, against the real server with FakeClaude: another profile's
/// sessions are not listed, and are refused as an unknown ID is, so a session cannot learn they exist; its roots are
/// not listed or started in, and its sessions are no parent. The server's credential, the user's own overseer, sees
/// every profile. One test per tool.
/// </summary>
public class FleetScopeTests
{
    private const string Unknown = "fleet/work/260101-work-nobody-zzzz";

    private static FakeScript Waiting() => new FakeScript().EmitInit().AwaitStdin().AwaitStdin();

    /// <summary>An overseer in <see cref="Profile"/> with its fleet client, a session of <see cref="OtherProfile"/>, and the user's fleet client.</summary>
    private sealed class Scoped : IAsyncDisposable
    {
        public FleetRun Run { get; private init; } = null!;
        public string OverseerId { get; private init; } = "";
        public McpClient Overseer { get; private init; } = null!;
        public string ForeignId { get; private init; } = "";
        public McpClient User { get; private init; } = null!;

        public static async Task<Scoped> StartAsync()
        {
            var run = await FleetRun.StartAsync(Waiting());
            var overseerId = await run.CreateOverHubAsync("overseer", OverseerAction);
            var entry = GodModeMcpEntry.FleetOf(await run.WaitForLaunchAsync(overseerId, launch => launch.Stdin.Count > 0));
            var foreignId = await run.CreateOverHubAsync("foreign", WorkAction, OtherProfile, OtherRoot);
            await run.WaitForLaunchAsync(foreignId, launch => launch.Stdin.Count > 0, root: OtherRoot);
            return new Scoped
            {
                Run = run, OverseerId = overseerId, Overseer = await ConnectAsync(entry), ForeignId = foreignId,
                User = await run.ConnectFleetAsync(),
            };
        }

        /// <summary>Asserts the overseer's call on the foreign session is refused just as one on an unknown ID is.</summary>
        public async Task AssertRefusedAsUnknownAsync(string tool, Dictionary<string, object?>? more = null)
        {
            Dictionary<string, object?> On(string session)
            {
                var arguments = new Dictionary<string, object?> { ["session"] = session };
                foreach (var (key, value) in more ?? []) arguments[key] = value;
                return arguments;
            }
            var foreign = await Run.RefusedAsync(Overseer, tool, On(ForeignId));
            var unknown = await Run.RefusedAsync(Overseer, tool, On(Unknown));
            Assert.Equal(unknown.Replace(Unknown, "<id>"), foreign.Replace(ForeignId, "<id>"));
        }

        public async Task<ProjectStatus> ForeignAsync() =>
            await Run.Client.Hub.InvokeAsync<ProjectStatus>(nameof(IProjectHub.GetStatus), ForeignId);

        public async ValueTask DisposeAsync()
        {
            await Overseer.DisposeAsync();
            await User.DisposeAsync();
            await Run.DisposeAsync();
        }
    }

    private static string[] Ids(JsonElement sessions) => sessions.EnumerateArray().Select(s => s.GetProperty("Id").GetString()!).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task ListSessions_OfASession_IsItsProfilesAlone()
    {
        await using var scoped = await Scoped.StartAsync();

        Assert.Equal([scoped.OverseerId], Ids(await scoped.Run.CallAsync(scoped.Overseer, "list_sessions")));
        Assert.Equal(new[] { scoped.ForeignId, scoped.OverseerId }.Order(StringComparer.Ordinal),
            Ids(await scoped.Run.CallAsync(scoped.User, "list_sessions")));
    }

    [Fact]
    public async Task ListRoots_OfASession_IsItsProfilesAlone()
    {
        await using var scoped = await Scoped.StartAsync();

        static (string[] Profiles, string[] Roots) Of(JsonElement listed) => (
            listed.GetProperty("Profiles").EnumerateArray().Select(p => p.GetProperty("Name").GetString()!).Order(StringComparer.Ordinal).ToArray(),
            listed.GetProperty("Roots").EnumerateArray().Select(r => r.GetProperty("Name").GetString()!).Order(StringComparer.Ordinal).ToArray());

        var own = Of(await scoped.Run.CallAsync(scoped.Overseer, "list_roots"));
        Assert.Equal([Profile], own.Profiles);
        Assert.Equal([RootName], own.Roots);
        var all = Of(await scoped.Run.CallAsync(scoped.User, "list_roots"));
        Assert.Contains(OtherProfile, all.Profiles);
        Assert.Contains(OtherRoot, all.Roots);
    }

    [Fact]
    public async Task StartSession_OfASession_IsRefusedInAnotherProfilesRoot_AndUnderAnotherProfilesParent()
    {
        await using var scoped = await Scoped.StartAsync();
        var run = scoped.Run;
        var before = Ids(await run.CallAsync(scoped.User, "list_sessions"));

        var inOther = StartArguments("w", WorkAction);
        inOther["profile"] = OtherProfile;
        inOther["root"] = OtherRoot;
        Assert.Contains("own profile", await run.RefusedAsync(scoped.Overseer, "start_session", inOther));

        // Refused as a parent this server does not have is
        var underForeign = await run.RefusedAsync(scoped.Overseer, "start_session", StartArguments("w", WorkAction, new() { ["parent"] = scoped.ForeignId }));
        var underUnknown = await run.RefusedAsync(scoped.Overseer, "start_session", StartArguments("w", WorkAction, new() { ["parent"] = Unknown }));
        Assert.Equal(underUnknown.Replace(Unknown, "<id>"), underForeign.Replace(scoped.ForeignId, "<id>"));

        Assert.Equal(before, Ids(await run.CallAsync(scoped.User, "list_sessions")));
        // The user's own overseer starts in any profile
        Assert.NotNull((await run.CallAsync(scoped.User, "start_session", inOther)).GetProperty("Id").GetString());
    }

    [Fact]
    public async Task Send_OfASession_ToAnotherProfilesSession_IsRefusedAsAnUnknownIDIs_AndReachesNothing()
    {
        await using var scoped = await Scoped.StartAsync();

        await scoped.AssertRefusedAsUnknownAsync("send", new() { ["text"] = "Do something else" });

        var launch = await scoped.Run.WaitForLaunchAsync(scoped.ForeignId, _ => true, root: OtherRoot);
        Assert.Single(launch.Stdin);
    }

    [Fact]
    public async Task Read_OfASession_OfAnotherProfilesSession_IsRefusedAsAnUnknownIDIs()
    {
        await using var scoped = await Scoped.StartAsync();

        await scoped.AssertRefusedAsUnknownAsync("read");
        Assert.Equal(scoped.ForeignId, (await scoped.Run.CallAsync(scoped.User, "read", new() { ["session"] = scoped.ForeignId })).GetProperty("Id").GetString());
    }

    [Fact]
    public async Task Stop_OfASession_OfAnotherProfilesSession_IsRefusedAsAnUnknownIDIs_AndStopsNothing()
    {
        await using var scoped = await Scoped.StartAsync();

        await scoped.AssertRefusedAsUnknownAsync("stop");

        Assert.NotEqual(ProjectState.Stopped, (await scoped.ForeignAsync()).State);
    }

    [Fact]
    public async Task Resume_OfASession_OfAnotherProfilesSession_IsRefusedAsAnUnknownIDIs_AndResumesNothing()
    {
        await using var scoped = await Scoped.StartAsync();
        await scoped.Run.CallAsync(scoped.User, "stop", new() { ["session"] = scoped.ForeignId });

        await scoped.AssertRefusedAsUnknownAsync("resume");

        Assert.Equal(ProjectState.Stopped, (await scoped.ForeignAsync()).State);
    }
}
