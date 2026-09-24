using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodMode.ClientBase.Services;

/// <summary>
/// Picks the URL to reach a server on: probes /health on every URL at once and returns
/// the first URL, in the entry's order, that answers successfully within the timeout (about 1.5 s).
/// </summary>
public sealed class ServerUrlSelector(HttpClient http, TimeSpan? timeout = null, ILogger<ServerUrlSelector>? logger = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(1.5);

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

    /// <summary>
    /// An HttpClient for the probes that connects to all of a host's addresses at once and keeps the first
    /// that accepts. "localhost" resolves to ::1 first, and on Windows a refused connect takes about 2 s,
    /// longer than the timeout, so trying the addresses in turn would call a server on 127.0.0.1 unreachable.
    /// </summary>
    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler { ConnectCallback = ConnectToFirstAddressAsync });

    private static async ValueTask<Stream> ConnectToFirstAddressAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var attempts = addresses.Select(async address =>
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cts.Token);
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }).ToList();

        var errors = new List<Exception>();
        while (attempts.Count > 0)
        {
            var done = await Task.WhenAny(attempts);
            attempts.Remove(done);
            if (done.IsCompletedSuccessfully)
            {
                await cts.CancelAsync();
                foreach (var loser in attempts)
                    _ = loser.ContinueWith(t => t.Result.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
                return new NetworkStream(done.Result, ownsSocket: true);
            }
            errors.Add(done.Exception?.InnerException ?? new OperationCanceledException());
        }
        throw new AggregateException($"Could not connect to {context.DnsEndPoint}", errors);
    }

    /// <summary>The first reachable URL (without a trailing slash), or null when none answers.</summary>
    public async Task<string?> SelectAsync(IReadOnlyList<string> urls, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        var probes = urls.Select(u => ProbeAsync(u.TrimEnd('/'), cts.Token)).ToList();
        try
        {
            for (var i = 0; i < urls.Count; i++)
                if (await probes[i])
                    return urls[i].TrimEnd('/');
            return null;
        }
        finally
        {
            await cts.CancelAsync();
        }
    }

    private async Task<bool> ProbeAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync($"{url}/health", ct);
            _logger.LogDebug("Health check {Url}/health -> {StatusCode}", url, response.StatusCode);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Health check {Url}/health failed: {Error}", url, ex.Message);
            return false;
        }
    }
}
