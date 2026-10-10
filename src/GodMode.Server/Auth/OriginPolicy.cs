namespace GodMode.Server.Auth;

/// <summary>
/// No browser is a client of the server: it serves no page, and the app's page reaches it only through
/// the app's relay. A browser sends an <c>Origin</c> on every WebSocket upgrade and on any request but a
/// same-origin GET, so any request that carries one, whatever it names, is refused with 403 before
/// authentication. A request with no <c>Origin</c> (the MAUI relay, the app's attention service, a
/// project's claude on the MCP endpoint, curl) needs its credential alone.
/// </summary>
public static class OriginPolicy
{
    /// <summary>Refuses (403) every request with an <c>Origin</c>, ahead of everything else in the pipeline.</summary>
    public static WebApplication UseOriginPolicy(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(OriginPolicy));

        app.Use(async (context, next) =>
        {
            var origin = context.Request.Headers.Origin;
            if (origin.Count == 0)
            {
                await next(context);
                return;
            }

            // The path alone: a caller may have put a key in the query string, which no endpoint reads
            logger.LogWarning("Refused {Method} {Path} from origin {Origin}: no browser is a client of this server; use the GodMode app",
                context.Request.Method, context.Request.Path, origin.ToString());
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
        });
        return app;
    }
}
