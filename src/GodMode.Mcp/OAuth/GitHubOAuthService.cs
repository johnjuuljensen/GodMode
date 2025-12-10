using System.Net.Http.Headers;
using System.Text.Json;

namespace GodMode.Mcp.OAuth;

public class GitHubOAuthService
{
    private readonly HttpClient _httpClient;
    private readonly string _clientId;
    private readonly string _clientSecret;

    public GitHubOAuthService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _clientId = configuration["GitHub:ClientId"]
            ?? throw new InvalidOperationException("GitHub:ClientId not configured");
        _clientSecret = configuration["GitHub:ClientSecret"]
            ?? throw new InvalidOperationException("GitHub:ClientSecret not configured");
    }

    public string GetAuthorizationUrl(string state, string redirectUri)
    {
        var scopes = "codespace,read:user";
        return $"https://github.com/login/oauth/authorize?client_id={_clientId}&redirect_uri={Uri.EscapeDataString(redirectUri)}&scope={scopes}&state={state}";
    }

    public async Task<GitHubTokenResponse?> ExchangeCodeForTokenAsync(string code)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["code"] = code
            })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<GitHubTokenResponse>(json);
    }

    public async Task<GitHubUser?> GetUserAsync(string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("GodMode-MCP", "1.0"));

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<GitHubUser>(json);
    }
}

public class GitHubTokenResponse
{
    public string? access_token { get; set; }
    public string? token_type { get; set; }
    public string? scope { get; set; }
    public string? error { get; set; }
    public string? error_description { get; set; }
}

public class GitHubUser
{
    public long id { get; set; }
    public string? login { get; set; }
    public string? name { get; set; }
    public string? email { get; set; }
}
