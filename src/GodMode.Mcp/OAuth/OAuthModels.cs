using System.Text.Json.Serialization;

namespace GodMode.Mcp.OAuth;

public record OAuthClient(
    string ClientId,
    string? ClientSecret,
    string ClientName,
    List<string> RedirectUris,
    DateTime CreatedAt
);

public record AuthorizationCode(
    string Code,
    string ClientId,
    string RedirectUri,
    string CodeChallenge,
    string CodeChallengeMethod,
    string? GitHubState,
    DateTime ExpiresAt
);

public record TokenRecord(
    string AccessToken,
    string RefreshToken,
    string ClientId,
    string GitHubUserId,
    string GitHubAccessToken,
    DateTime ExpiresAt
);

public class ClientRegistrationRequest
{
    [JsonPropertyName("client_name")]
    public string? ClientName { get; set; }

    [JsonPropertyName("redirect_uris")]
    public List<string>? RedirectUris { get; set; }

    [JsonPropertyName("grant_types")]
    public List<string>? GrantTypes { get; set; }

    [JsonPropertyName("response_types")]
    public List<string>? ResponseTypes { get; set; }

    [JsonPropertyName("token_endpoint_auth_method")]
    public string? TokenEndpointAuthMethod { get; set; }
}

public class ClientRegistrationResponse
{
    [JsonPropertyName("client_id")]
    public required string ClientId { get; set; }

    [JsonPropertyName("client_secret")]
    public string? ClientSecret { get; set; }

    [JsonPropertyName("client_name")]
    public string? ClientName { get; set; }

    [JsonPropertyName("redirect_uris")]
    public List<string>? RedirectUris { get; set; }

    [JsonPropertyName("client_id_issued_at")]
    public long ClientIdIssuedAt { get; set; }

    [JsonPropertyName("client_secret_expires_at")]
    public long ClientSecretExpiresAt { get; set; }
}

public class TokenRequest
{
    [JsonPropertyName("grant_type")]
    public string? GrantType { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("redirect_uri")]
    public string? RedirectUri { get; set; }

    [JsonPropertyName("client_id")]
    public string? ClientId { get; set; }

    [JsonPropertyName("client_secret")]
    public string? ClientSecret { get; set; }

    [JsonPropertyName("code_verifier")]
    public string? CodeVerifier { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }
}

public class TokenResponse
{
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; set; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "Bearer";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }
}

public class OAuthError
{
    [JsonPropertyName("error")]
    public required string Error { get; set; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }
}

public class AuthorizationServerMetadata
{
    [JsonPropertyName("issuer")]
    public required string Issuer { get; set; }

    [JsonPropertyName("authorization_endpoint")]
    public required string AuthorizationEndpoint { get; set; }

    [JsonPropertyName("token_endpoint")]
    public required string TokenEndpoint { get; set; }

    [JsonPropertyName("registration_endpoint")]
    public string? RegistrationEndpoint { get; set; }

    [JsonPropertyName("scopes_supported")]
    public List<string>? ScopesSupported { get; set; }

    [JsonPropertyName("response_types_supported")]
    public required List<string> ResponseTypesSupported { get; set; }

    [JsonPropertyName("grant_types_supported")]
    public List<string>? GrantTypesSupported { get; set; }

    [JsonPropertyName("token_endpoint_auth_methods_supported")]
    public List<string>? TokenEndpointAuthMethodsSupported { get; set; }

    [JsonPropertyName("code_challenge_methods_supported")]
    public List<string>? CodeChallengeMethodsSupported { get; set; }
}
