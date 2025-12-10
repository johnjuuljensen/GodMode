namespace GodMode.Mcp;

public class McpUserConfig
{
    public string? DefaultOwner { get; set; }
    public string? DefaultRepo { get; set; }
    public List<string> Devcontainers { get; set; } = [];

    public static McpUserConfig FromQuery(IQueryCollection query)
    {
        var config = new McpUserConfig();

        var owner = query["owner"].FirstOrDefault();
        var repo = query["repo"].FirstOrDefault();
        if (!string.IsNullOrEmpty(owner)) config.DefaultOwner = owner;
        if (!string.IsNullOrEmpty(repo)) config.DefaultRepo = repo;

        // Multiple devcontainer folder names allowed
        // e.g., ?devcontainer=frontend-dev&devcontainer=backend-dev
        foreach (var dc in query["devcontainer"])
        {
            if (!string.IsNullOrEmpty(dc))
                config.Devcontainers.Add(dc);
        }

        return config;
    }

    public string GetDevcontainerPath(string folderName)
        => $".devcontainer/{folderName}/devcontainer.json";
}
