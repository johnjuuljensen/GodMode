namespace GodMode.Mcp.OAuth;

public interface IOAuthStore
{
    Task<OAuthClient> CreateClientAsync(ClientRegistrationRequest request);
    Task<OAuthClient?> GetClientAsync(string clientId);

    Task<AuthorizationCode> CreateAuthorizationCodeAsync(
        string clientId,
        string redirectUri,
        string codeChallenge,
        string codeChallengeMethod,
        string? gitHubState);
    Task<AuthorizationCode?> GetAndDeleteAuthorizationCodeAsync(string code);

    Task<TokenRecord> CreateTokenAsync(string clientId, string gitHubUserId, string gitHubAccessToken);
    Task<TokenRecord?> GetTokenAsync(string accessToken);
    Task DeleteTokenAsync(string accessToken);

    Task StoreGitHubStateAsync(string state, string clientId, string redirectUri, string codeChallenge, string codeChallengeMethod);
    Task<(string ClientId, string RedirectUri, string CodeChallenge, string CodeChallengeMethod)?> GetGitHubStateAsync(string state);
}
