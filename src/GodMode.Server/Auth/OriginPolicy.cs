using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace GodMode.Server.Auth;

/// <summary>
/// The browser origins the server takes requests from. A browser sends an <c>Origin</c> on every
/// WebSocket upgrade and on any request but a same-origin GET; such a request is let through only
/// from one of the server's own origins:
/// <list type="bullet">
/// <item>the scheme, host and port of each address it listens on, where a loopback address, or a
/// wildcard (which listens on loopback too), also stands for <c>localhost</c>, <c>127.0.0.1</c> and
/// <c>[::1]</c> on its port;</item>
/// <item>those listed in <c>Authentication:AllowedOrigins</c> (a reverse proxy's, a host name's);</item>
/// <item>in a codespace, its forwarded port's (<c>https://{CODESPACE_NAME}-{port}.app.github.dev</c>);</item>
/// <item>in Development, the Vite dev server's.</item>
/// </list>
/// Anything else, <c>Origin: null</c> included, is refused with 403 before authentication. A request
/// with no <c>Origin</c> (the MAUI relay, the app's attention service, curl) needs the key alone.
/// </summary>
public sealed class OriginPolicy
{
    public const string AllowedOriginsSetting = "Authentication:AllowedOrigins";

    private static readonly string[] ViteOrigins = ["http://localhost:5173", "https://localhost:5173"];
    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "[::1]"];

    private readonly IReadOnlySet<string> _listed;
    private readonly string? _codespaceName;
    private readonly string _codespaceDomain;

    private OriginPolicy(IReadOnlySet<string> listed, string? codespaceName, string codespaceDomain)
    {
        _listed = listed;
        _codespaceName = codespaceName;
        _codespaceDomain = codespaceDomain;
    }

    /// <summary>
    /// The policy the configuration describes. <c>Authentication:AllowedOrigins</c> is a list (or one
    /// string, <c>;</c>-separated); an entry that is not an origin throws <see cref="StartupConfigurationException"/>.
    /// </summary>
    public static OriginPolicy From(IConfiguration config, bool isDevelopment, bool isCodespace)
    {
        var section = config.GetSection(AllowedOriginsSetting);
        var entries = section.Value is { } single
            ? single.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : section.GetChildren().Select(child => child.Value).OfType<string>().Where(value => value.Trim().Length > 0);

        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries.Concat(isDevelopment ? ViteOrigins : []))
            listed.Add(Normalize(entry.TrimEnd('/')) ?? throw new StartupConfigurationException(
                $"GodMode.Server will not start: {AllowedOriginsSetting} lists '{entry}', which is not an origin " +
                $"(scheme://host[:port], http or https, with no path)."));

        return new OriginPolicy(listed,
            isCodespace && config["CODESPACE_NAME"] is { Length: > 0 } name ? name : null,
            config["GITHUB_CODESPACES_PORT_FORWARDING_DOMAIN"] is { Length: > 0 } domain ? domain : "app.github.dev");
    }

    /// <summary>Every origin allowed while the server listens on <paramref name="listeningAddresses"/>, normalized as <see cref="Normalize"/> does.</summary>
    public IReadOnlySet<string> AllowedFor(IEnumerable<string> listeningAddresses)
    {
        var allowed = new HashSet<string>(_listed, StringComparer.Ordinal);
        foreach (var address in listeningAddresses)
        {
            if (Listening(address) is not var (scheme, host, port)) continue;
            // An IP literal other than a wildcard or loopback address listens there alone
            var ip = IPAddress.TryParse(host.Trim('[', ']'), out var parsed) ? parsed : null;
            var wildcard = host is "+" or "*" || IPAddress.Any.Equals(ip) || IPAddress.IPv6Any.Equals(ip);
            var onLoopback = ip == null || wildcard || IPAddress.IsLoopback(ip);

            if (!wildcard && Normalize($"{scheme}://{host}:{port}") is { } own) allowed.Add(own);
            if (onLoopback)
                foreach (var loopback in LoopbackHosts)
                    allowed.Add($"{scheme}://{loopback}:{port}");
            if (_codespaceName != null)
                allowed.Add($"https://{_codespaceName}-{port}.{_codespaceDomain}".ToLowerInvariant());
        }
        return allowed;
    }

    /// <summary>
    /// <c>scheme://host:port</c>, lowercase with the port explicit, for an http or https origin with no
    /// path, query, fragment or user info; null for anything else (<c>null</c>, a URL with a path, another scheme).
    /// </summary>
    public static string? Normalize(string origin) =>
        Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && uri.UserInfo.Length == 0 && uri.PathAndQuery == "/" && uri.Fragment.Length == 0
            ? $"{uri.Scheme}://{uri.Host}:{uri.Port}".ToLowerInvariant()
            : null;

    private static (string Scheme, string Host, int Port)? Listening(string address)
    {
        try
        {
            var binding = BindingAddress.Parse(address);
            return binding is { IsUnixPipe: false, IsNamedPipe: false, Scheme: "http" or "https" }
                ? (binding.Scheme, binding.Host, binding.Port)
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

public static class OriginPolicyExtensions
{
    /// <summary>
    /// Refuses (403) a request whose <c>Origin</c> the policy does not allow, ahead of everything else
    /// in the pipeline. The server's own origins are worked out on the first request, once it listens.
    /// </summary>
    public static WebApplication UseOriginPolicy(this WebApplication app, OriginPolicy policy)
    {
        var allowed = new Lazy<IReadOnlySet<string>>(() => policy.AllowedFor(ListeningAddresses(app)));
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<OriginPolicy>();

        app.Lifetime.ApplicationStarted.Register(() =>
            logger.LogInformation("Browser requests are accepted from {Origins}", string.Join(", ", allowed.Value.Order())));

        app.Use(async (context, next) =>
        {
            var origin = context.Request.Headers.Origin;
            if (origin.Count == 0
                || (origin is [{ } value] && OriginPolicy.Normalize(value) is { } normalized && allowed.Value.Contains(normalized)))
            {
                await next(context);
                return;
            }

            // The path alone: the hub's query string carries the key
            logger.LogWarning("Refused {Method} {Path} from origin {Origin}: not one of this server's origins. " +
                "An origin the server is reached by that it cannot tell (a reverse proxy's, a host name's) goes in {Setting}",
                context.Request.Method, context.Request.Path, origin.ToString(), OriginPolicy.AllowedOriginsSetting);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
        });
        return app;
    }

    private static IEnumerable<string> ListeningAddresses(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
}
