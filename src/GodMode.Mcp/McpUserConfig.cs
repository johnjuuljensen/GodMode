namespace GodMode.Mcp;

public class McpUserConfig
{
    public string? DefaultOwner { get; set; }
    public string? DefaultRepo { get; set; }

    public static McpUserConfig FromQuery(IQueryCollection query)
    {
        var config = new McpUserConfig();

        // Individual params override
        var owner = query["owner"].FirstOrDefault();
        var repo = query["repo"].FirstOrDefault();
        if (!string.IsNullOrEmpty(owner)) config.DefaultOwner = owner;
        if (!string.IsNullOrEmpty(repo)) config.DefaultRepo = repo;

        return config;
    }
}
