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
