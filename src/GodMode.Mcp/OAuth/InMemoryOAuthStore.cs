using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace GodMode.Mcp.OAuth;

public class InMemoryOAuthStore : IOAuthStore
{
    private readonly ConcurrentDictionary<string, OAuthClient> _clients = new();
    private readonly ConcurrentDictionary<string, AuthorizationCode> _authCodes = new();
    private readonly ConcurrentDictionary<string, TokenRecord> _tokens = new();
    private readonly ConcurrentDictionary<string, string> _refreshTokens = new(); // refreshToken -> accessToken
    private readonly ConcurrentDictionary<string, (string ClientId, string RedirectUri, string CodeChallenge, string CodeChallengeMethod)> _gitHubStates = new();

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

        _clients[clientId] = client;
        return Task.FromResult(client);
    }

    public Task<OAuthClient?> GetClientAsync(string clientId)
    {
        _clients.TryGetValue(clientId, out var client);
        return Task.FromResult(client);
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

        _authCodes[code] = authCode;
        return Task.FromResult(authCode);
    }

    public Task<AuthorizationCode?> GetAndDeleteAuthorizationCodeAsync(string code)
    {
        if (!_authCodes.TryRemove(code, out var authCode))
            return Task.FromResult<AuthorizationCode?>(null);

        if (authCode.ExpiresAt < DateTime.UtcNow)
            return Task.FromResult<AuthorizationCode?>(null);

        return Task.FromResult<AuthorizationCode?>(authCode);
    }

    public Task<TokenRecord> CreateTokenAsync(string clientId, string gitHubUserId, string gitHubAccessToken)
    {
        var accessToken = GenerateSecureToken();
        var refreshToken = GenerateSecureToken();
        var token = new TokenRecord(
            accessToken,
            refreshToken,
            clientId,
            gitHubUserId,
            gitHubAccessToken,
            DateTime.UtcNow.AddHours(1)
        );

        _tokens[accessToken] = token;
        _refreshTokens[refreshToken] = accessToken;
        return Task.FromResult(token);
    }

    public Task<TokenRecord?> GetTokenAsync(string accessToken)
    {
        if (!_tokens.TryGetValue(accessToken, out var token))
            return Task.FromResult<TokenRecord?>(null);

        if (token.ExpiresAt < DateTime.UtcNow)
        {
            _tokens.TryRemove(accessToken, out _);
            _refreshTokens.TryRemove(token.RefreshToken, out _);
            return Task.FromResult<TokenRecord?>(null);
        }

        return Task.FromResult<TokenRecord?>(token);
    }

    public Task<TokenRecord?> GetTokenByRefreshTokenAsync(string refreshToken)
    {
        if (!_refreshTokens.TryGetValue(refreshToken, out var accessToken))
            return Task.FromResult<TokenRecord?>(null);

        if (!_tokens.TryGetValue(accessToken, out var token))
            return Task.FromResult<TokenRecord?>(null);

        return Task.FromResult<TokenRecord?>(token);
    }

    public Task DeleteTokenAsync(string accessToken)
    {
        if (_tokens.TryRemove(accessToken, out var token))
        {
            _refreshTokens.TryRemove(token.RefreshToken, out _);
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
        _gitHubStates[state] = (clientId, redirectUri, codeChallenge, codeChallengeMethod);
        return Task.CompletedTask;
    }

    public Task<(string ClientId, string RedirectUri, string CodeChallenge, string CodeChallengeMethod)?> GetGitHubStateAsync(string state)
    {
        if (!_gitHubStates.TryRemove(state, out var data))
            return Task.FromResult<(string, string, string, string)?>(null);

        return Task.FromResult<(string, string, string, string)?>(data);
    }

    private static string GenerateSecureToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }
}
