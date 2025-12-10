using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace GodMode.Mcp.Resources;

[McpServerResourceType]
public class ConfigResources
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ConfigResources(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerResource(UriTemplate = "config://default")]
    [Description("Returns the default configuration for this MCP server, including default owner, repo, and devcontainer configurations")]
    public string GetDefaultConfig()
    {
        var query = _httpContextAccessor.HttpContext?.Request.Query;
        var config = query != null ? McpUserConfig.FromQuery(query) : new McpUserConfig();

        return JsonSerializer.Serialize(new
        {
            defaultOwner = config.DefaultOwner,
            defaultRepo = config.DefaultRepo,
            hasDefaults = config.DefaultOwner != null && config.DefaultRepo != null,
            devcontainers = config.Devcontainers.Select(dc => new
            {
                name = dc,
                path = config.GetDevcontainerPath(dc)
            }).ToList()
        });
    }
}
