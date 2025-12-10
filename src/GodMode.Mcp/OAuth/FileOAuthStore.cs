using System.Security.Cryptography;
using System.Text.Json;

namespace GodMode.Mcp.OAuth;

public class FileOAuthStore : IOAuthStore
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private StoreData _data;

    public FileOAuthStore(IConfiguration configuration)
    {
        _filePath = configuration["Storage:FilePath"]
            ?? Path.Combine(AppContext.BaseDirectory, "oauth-store.json");

        _data = Load();
    }

    public Task<OAuthClient> CreateClientAsync(ClientRegistrationRequest request)
    {
        var clientId = GenerateSecureToken();
        var clientSecret = GenerateSecureToken();

        var client = new OAuthClient(
            clientId,
            clientSecret,
            request.ClientName ?? "Unknown",
            request.RedirectUris ?? [],
            DateTime.UtcNow
        );

        lock (_lock)
        {
            _data.Clients[clientId] = client;
            Save();
        }

        return Task.FromResult(client);
    }

    public Task<OAuthClient?> GetClientAsync(string clientId)
    {
        lock (_lock)
        {
            _data.Clients.TryGetValue(clientId, out var client);
            return Task.FromResult(client);
        }
    }

    public Task<AuthorizationCode> CreateAuthorizationCodeAsync(
        string clientId,
        string redirectUri,
        string codeChallenge,
        string codeChallengeMethod,
        string? gitHubState)
    {
        var code = GenerateSecureToken();
        var authCode = new AuthorizationCode(
            code,
            clientId,
            redirectUri,
            codeChallenge,
            codeChallengeMethod,
            gitHubState,
            DateTime.UtcNow.AddMinutes(10)
        );

        lock (_lock)
        {
            CleanExpired();
            _data.AuthCodes[code] = authCode;
            Save();
        }

        return Task.FromResult(authCode);
    }

    public Task<AuthorizationCode?> GetAndDeleteAuthorizationCodeAsync(string code)
    {
        lock (_lock)
        {
            if (!_data.AuthCodes.TryGetValue(code, out var authCode))
                return Task.FromResult<AuthorizationCode?>(null);

            _data.AuthCodes.Remove(code);
            Save();

            if (authCode.ExpiresAt < DateTime.UtcNow)
                return Task.FromResult<AuthorizationCode?>(null);

            return Task.FromResult<AuthorizationCode?>(authCode);
        }
    }

    public Task<TokenRecord> CreateTokenAsync(string clientId, string gitHubUserId, string gitHubAccessToken)
    {
        var accessToken = GenerateSecureToken();
        var token = new TokenRecord(
            accessToken,
            clientId,
            gitHubUserId,
            gitHubAccessToken,
            DateTime.UtcNow.AddHours(1)
        );

        lock (_lock)
        {
            CleanExpired();
            _data.Tokens[accessToken] = token;
            Save();
        }

        return Task.FromResult(token);
    }

    public Task<TokenRecord?> GetTokenAsync(string accessToken)
    {
        lock (_lock)
        {
            if (!_data.Tokens.TryGetValue(accessToken, out var token))
                return Task.FromResult<TokenRecord?>(null);

            if (token.ExpiresAt < DateTime.UtcNow)
            {
                _data.Tokens.Remove(accessToken);
                Save();
                return Task.FromResult<TokenRecord?>(null);
            }

            return Task.FromResult<TokenRecord?>(token);
        }
    }

    public Task DeleteTokenAsync(string accessToken)
    {
        lock (_lock)
        {
            _data.Tokens.Remove(accessToken);
            Save();
        }

        return Task.CompletedTask;
    }

    public Task StoreGitHubStateAsync(
        string state,
        string clientId,
        string redirectUri,
        string codeChallenge,
        string codeChallengeMethod)
    {
        lock (_lock)
        {
            CleanExpired();
            _data.GitHubStates[state] = new GitHubStateRecord(
                clientId,
                redirectUri,
                codeChallenge,
                codeChallengeMethod,
                DateTime.UtcNow.AddMinutes(10)
            );
            Save();
        }

        return Task.CompletedTask;
    }

    public Task<(string ClientId, string RedirectUri, string CodeChallenge, string CodeChallengeMethod)?> GetGitHubStateAsync(string state)
    {
        lock (_lock)
        {
            if (!_data.GitHubStates.TryGetValue(state, out var record))
                return Task.FromResult<(string, string, string, string)?>(null);

            _data.GitHubStates.Remove(state);
            Save();

            if (record.ExpiresAt < DateTime.UtcNow)
                return Task.FromResult<(string, string, string, string)?>(null);

            return Task.FromResult<(string, string, string, string)?>(
                (record.ClientId, record.RedirectUri, record.CodeChallenge, record.CodeChallengeMethod));
        }
    }

    private StoreData Load()
    {
        if (!File.Exists(_filePath))
            return new StoreData();

        var json = File.ReadAllText(_filePath);
        return JsonSerializer.Deserialize<StoreData>(json) ?? new StoreData();
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }

    private void CleanExpired()
    {
        var now = DateTime.UtcNow;

        var expiredCodes = _data.AuthCodes.Where(x => x.Value.ExpiresAt < now).Select(x => x.Key).ToList();
        foreach (var key in expiredCodes)
            _data.AuthCodes.Remove(key);

        var expiredTokens = _data.Tokens.Where(x => x.Value.ExpiresAt < now).Select(x => x.Key).ToList();
        foreach (var key in expiredTokens)
            _data.Tokens.Remove(key);

        var expiredStates = _data.GitHubStates.Where(x => x.Value.ExpiresAt < now).Select(x => x.Key).ToList();
        foreach (var key in expiredStates)
            _data.GitHubStates.Remove(key);
    }

    private static string GenerateSecureToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private class StoreData
    {
        public Dictionary<string, OAuthClient> Clients { get; set; } = new();
        public Dictionary<string, AuthorizationCode> AuthCodes { get; set; } = new();
        public Dictionary<string, TokenRecord> Tokens { get; set; } = new();
        public Dictionary<string, GitHubStateRecord> GitHubStates { get; set; } = new();
    }

    private record GitHubStateRecord(
        string ClientId,
        string RedirectUri,
        string CodeChallenge,
        string CodeChallengeMethod,
        DateTime ExpiresAt
    );
}
