using System.ComponentModel;
using ModelContextProtocol.Server;
using Octokit;

namespace GodMode.Mcp.Tools;

[McpServerToolType]
public class GitHubTools
{
    private readonly IGitHubClient _gitHubClient;

    public GitHubTools(IGitHubClient gitHubClient)
    {
        _gitHubClient = gitHubClient;
    }

    [McpServerTool]
    [Description("Lists repositories accessible to the authenticated user")]
    public async Task<ListRepositoriesResult> ListRepositories(
        [Description("Filter by repository type: all, owner, public, private, member")] string? type = "all",
        [Description("Sort by: created, updated, pushed, full_name")] string? sort = "updated",
        [Description("Sort direction: asc or desc")] string? direction = "desc",
        [Description("Maximum number of repositories to return (1-100)")] int? perPage = 30,
        [Description("Page number for pagination")] int? page = 1)
    {
        var request = new RepositoryRequest
        {
            Type = ParseRepositoryType(type),
            Sort = ParseRepositorySort(sort),
            Direction = ParseSortDirection(direction)
        };

        var options = new ApiOptions
        {
            PageSize = Math.Clamp(perPage ?? 30, 1, 100),
            PageCount = 1,
            StartPage = page ?? 1
        };

        var repos = await _gitHubClient.Repository.GetAllForCurrent(request, options);

        return new ListRepositoriesResult
        {
            Repositories = repos.Select(r => new RepositoryInfo
            {
                FullName = r.FullName,
                Description = r.Description,
                Private = r.Private,
                Fork = r.Fork,
                DefaultBranch = r.DefaultBranch,
                Language = r.Language,
                StargazersCount = r.StargazersCount,
                ForksCount = r.ForksCount,
                OpenIssuesCount = r.OpenIssuesCount,
                UpdatedAt = r.UpdatedAt.ToString("O"),
                HtmlUrl = r.HtmlUrl
            }).ToList(),
            Count = repos.Count
        };
    }

    private static RepositoryType ParseRepositoryType(string? type) => type?.ToLowerInvariant() switch
    {
        "owner" => RepositoryType.Owner,
        "public" => RepositoryType.Public,
        "private" => RepositoryType.Private,
        "member" => RepositoryType.Member,
        _ => RepositoryType.All
    };

    private static RepositorySort ParseRepositorySort(string? sort) => sort?.ToLowerInvariant() switch
    {
        "created" => RepositorySort.Created,
        "pushed" => RepositorySort.Pushed,
        "full_name" => RepositorySort.FullName,
        _ => RepositorySort.Updated
    };

    private static SortDirection ParseSortDirection(string? direction) => direction?.ToLowerInvariant() switch
    {
        "asc" => SortDirection.Ascending,
        _ => SortDirection.Descending
    };
}

public class ListRepositoriesResult
{
    public required List<RepositoryInfo> Repositories { get; set; }
    public int Count { get; set; }
}

public class RepositoryInfo
{
    public required string FullName { get; set; }
    public string? Description { get; set; }
    public bool Private { get; set; }
    public bool Fork { get; set; }
    public string? DefaultBranch { get; set; }
    public string? Language { get; set; }
    public int StargazersCount { get; set; }
    public int ForksCount { get; set; }
    public int OpenIssuesCount { get; set; }
    public string? UpdatedAt { get; set; }
    public string? HtmlUrl { get; set; }
}
