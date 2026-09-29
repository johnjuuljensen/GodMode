using System.Collections.Concurrent;
using GodMode.ClientBase.Hub;
using GodMode.ClientBase.Services;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace GodMode.ClientBase.Layout;

/// <summary>
/// The profiles the servers have (by name without case), and whether every server answered: only then does a profile
/// missing from <see cref="Names"/> not exist.
/// </summary>
public sealed record ProfileCensus(IReadOnlySet<string> Names, bool Complete)
{
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Asks every server the directory lists for its profiles (<see cref="IProjectHub.ListProfiles"/>), over a
    /// connection of its own, and waits until each has answered or <paramref name="wait"/> is up. A registration whose
    /// listing fails, or a server that doesn't answer in time (stopped, offline, slow), leaves it incomplete.
    /// </summary>
    public static async Task<ProfileCensus> TakeAsync(IServerDirectory directory, ILoggerFactory loggerFactory,
        TimeSpan? wait = null, TimeSpan? retryDelay = null, CancellationToken ct = default)
    {
        var logger = loggerFactory.CreateLogger<ProfileCensus>();
        var handler = new Handler();
        await using var connections = new ServerConnections(directory, handler, logger, "Profiles", retryDelay);
        await connections.RefreshAsync(ct);
        if (handler.Listed is null)
        {
            logger.LogInformation("Profiles: a registration's servers could not be listed; every saved window is kept");
            return new(handler.Names, Complete: false);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait ?? DefaultWait);
        try
        {
            await handler.Answered.Task.WaitAsync(timeout.Token);
            return new(handler.Names, Complete: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var missing = handler.Listed.Except(handler.AnsweredBy).ToList();
            logger.LogInformation("Profiles: {Count} server(s) did not answer in time ({Servers}); every saved window is kept",
                missing.Count, string.Join(", ", missing));
            return new(handler.Names, Complete: false);
        }
    }

    private sealed class Handler : IServerConnectionHandler
    {
        private readonly ConcurrentDictionary<string, ProfileInfo[]> _answers = new();

        public volatile IReadOnlySet<string>? Listed;
        public TaskCompletionSource Answered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IEnumerable<string> AnsweredBy => _answers.Keys;

        public IReadOnlySet<string> Names =>
            _answers.Values.SelectMany(p => p).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        public void Configure(ConnectedServer server, HubConnection connection) { }

        public async Task OnConnectedAsync(ConnectedServer server, HubConnection connection, CancellationToken ct)
        {
            _answers[server.Id] = await connection.InvokeAsync<ProfileInfo[]>(nameof(IProjectHub.ListProfiles), ct);
            Check();
        }

        public void OnRemoved(string serverId) => _answers.TryRemove(serverId, out _);

        public void OnListedCompletely(IReadOnlySet<string> serverIds)
        {
            Listed = serverIds;
            Check();
        }

        private void Check()
        {
            if (Listed is { } listed && listed.All(_answers.ContainsKey)) Answered.TrySetResult();
        }
    }
}
