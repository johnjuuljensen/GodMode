using GodMode.Server.Auth;
using ModelContextProtocol.AspNetCore;

namespace GodMode.Server.Services;

/// <summary>
/// GodMode's two MCP endpoints, one MCP server behind both. <see cref="McpEndpointUrl.Path"/> is its sessions'
/// claude's, with their project token, and serves the permission prompt alone. <see cref="FleetPath"/> is the
/// fleet's, with the server's own credential (as the hub), and serves <see cref="FleetTools"/>. Each tool type's
/// <c>[Authorize]</c> policy is its endpoint's, so each endpoint lists and calls only its own tools.
/// Stateless: claude's calls need no session (Claude Code speaks the sessionless 2026-07-28 revision), and a
/// waiting call keeps its own response stream, which carries its progress and its answer.
/// </summary>
public static class GodModeMcp
{
    /// <summary>Where the fleet's endpoint is mapped.</summary>
    public const string FleetPath = "/mcp/fleet";

    public static IServiceCollection AddGodModeMcp(this IServiceCollection services)
    {
        services.AddMcpServer(options => options.ServerInfo = new() { Name = ProjectManager.McpServerName, Version = "1.0.0" })
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .AddAuthorizationFilters()
            .WithTools<PermissionPromptTool>()
            .WithTools<FleetTools>();
        return services;
    }

    public static IEndpointRouteBuilder MapGodModeMcp(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMcp(McpEndpointUrl.Path).RequireAuthorization(GodModeAuthExtensions.ProjectPolicy);
        endpoints.MapMcp(FleetPath).RequireAuthorization(GodModeAuthExtensions.FleetPolicy);
        return endpoints;
    }
}
