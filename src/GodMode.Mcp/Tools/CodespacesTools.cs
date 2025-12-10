using System.ComponentModel;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace GodMode.Mcp.Tools;

[McpServerToolType]
public class CodespacesTools
{
    private readonly HttpClient _httpClient;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CodespacesTools(HttpClient httpClient, IHttpContextAccessor httpContextAccessor)
    {
        _httpClient = httpClient;
        _httpContextAccessor = httpContextAccessor;
    }

    private McpUserConfig GetUserConfig()
    {
        var query = _httpContextAccessor.HttpContext?.Request.Query;
        return query != null ? McpUserConfig.FromQuery(query) : new McpUserConfig();
    }

    [McpServerTool]
    [Description("Lists codespaces for the authenticated user")]
    public async Task<ListCodespacesResult> ListCodespaces(
        [Description("Maximum number of codespaces to return (1-100)")] int? perPage = 30,
        [Description("Page number for pagination")] int? page = 1)
    {
        var request = CreateRequest(HttpMethod.Get,
            $"https://api.github.com/user/codespaces?per_page={Math.Clamp(perPage ?? 30, 1, 100)}&page={page ?? 1}");

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GitHubCodespacesResponse>(json)!;

        return new ListCodespacesResult
        {
            Codespaces = result.Codespaces.Select(c => new CodespaceInfo
            {
                Name = c.Name,
                DisplayName = c.DisplayName,
                State = c.State,
                Repository = c.Repository?.FullName,
                Branch = c.GitStatus?.Ref,
                MachineName = c.Machine?.Name,
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt,
                WebUrl = c.WebUrl
            }).ToList(),
            TotalCount = result.TotalCount
        };
    }

    [McpServerTool]
    [Description("Starts an existing codespace")]
    public async Task<CodespaceInfo> StartCodespace(
        [Description("The name of the codespace to start")] string codespaceName)
    {
        var request = CreateRequest(HttpMethod.Post,
            $"https://api.github.com/user/codespaces/{codespaceName}/start");

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var c = JsonSerializer.Deserialize<GitHubCodespace>(json)!;

        return new CodespaceInfo
        {
            Name = c.Name,
            DisplayName = c.DisplayName,
            State = c.State,
            Repository = c.Repository?.FullName,
            Branch = c.GitStatus?.Ref,
            MachineName = c.Machine?.Name,
            CreatedAt = c.CreatedAt,
            UpdatedAt = c.UpdatedAt,
            WebUrl = c.WebUrl
        };
    }

    [McpServerTool]
    [Description("Lists available machine types that an existing codespace can transition to")]
    public async Task<ListMachinesResult> ListMachinesForCodespace(
        [Description("The name of the codespace (e.g., 'urban-space-barnacle-9xqjqqxg4rf7r99')")] string codespaceName)
    {
        var url = $"https://api.github.com/user/codespaces/{codespaceName}/machines";
        var request = CreateRequest(HttpMethod.Get, url);

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"GitHub API returned {(int)response.StatusCode} for {url}: {errorBody}");
        }

        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GitHubMachinesResponse>(json)!;

        return new ListMachinesResult
        {
            Machines = result.Machines.Select(m => new MachineInfo
            {
                Name = m.Name,
                DisplayName = m.DisplayName,
                Cpus = m.Cpus,
                MemoryInBytes = m.MemoryInBytes,
                StorageInBytes = m.StorageInBytes
            }).ToList(),
            TotalCount = result.TotalCount
        };
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var token = _httpContextAccessor.HttpContext?.User.FindFirst("github_access_token")?.Value
            ?? throw new InvalidOperationException("No GitHub token available");

        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("GodMode-MCP", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        return request;
    }
}

public class ListCodespacesResult
{
    public required List<CodespaceInfo> Codespaces { get; set; }
    public int TotalCount { get; set; }
}

public class CodespaceInfo
{
    public string? Name { get; set; }
    public string? DisplayName { get; set; }
    public string? State { get; set; }
    public string? Repository { get; set; }
    public string? Branch { get; set; }
    public string? MachineName { get; set; }
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
    public string? WebUrl { get; set; }
}

public class ListMachinesResult
{
    public required List<MachineInfo> Machines { get; set; }
    public int TotalCount { get; set; }
}

public class MachineInfo
{
    public string? Name { get; set; }
    public string? DisplayName { get; set; }
    public int Cpus { get; set; }
    public long MemoryInBytes { get; set; }
    public long StorageInBytes { get; set; }
}

// GitHub API response models
internal class GitHubCodespacesResponse
{
    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    [JsonPropertyName("codespaces")]
    public List<GitHubCodespace> Codespaces { get; set; } = [];
}

internal class GitHubCodespace
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("repository")]
    public GitHubRepository? Repository { get; set; }

    [JsonPropertyName("git_status")]
    public GitHubGitStatus? GitStatus { get; set; }

    [JsonPropertyName("machine")]
    public GitHubMachine? Machine { get; set; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; set; }

    [JsonPropertyName("web_url")]
    public string? WebUrl { get; set; }
}

internal class GitHubRepository
{
    [JsonPropertyName("full_name")]
    public string? FullName { get; set; }
}

internal class GitHubGitStatus
{
    [JsonPropertyName("ref")]
    public string? Ref { get; set; }
}

internal class GitHubMachine
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("cpus")]
    public int Cpus { get; set; }

    [JsonPropertyName("memory_in_bytes")]
    public long MemoryInBytes { get; set; }

    [JsonPropertyName("storage_in_bytes")]
    public long StorageInBytes { get; set; }
}

internal class GitHubMachinesResponse
{
    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    [JsonPropertyName("machines")]
    public List<GitHubMachine> Machines { get; set; } = [];
}

