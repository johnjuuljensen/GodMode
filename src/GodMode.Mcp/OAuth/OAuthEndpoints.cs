using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace GodMode.Mcp.OAuth;

public static class OAuthEndpoints
{
    public static void MapOAuthEndpoints(this WebApplication app)
    {
        app.MapGet("/.well-known/oauth-authorization-server", GetMetadata);
        app.MapPost("/register", RegisterClient);
        app.MapGet("/authorize", Authorize);
        app.MapGet("/callback", Callback);
        app.MapPost("/token", ExchangeToken);
    }

    private static AuthorizationServerMetadata GetMetadata(HttpRequest request)
    {
        var baseUrl = $"{request.Scheme}://{request.Host}";

        return new AuthorizationServerMetadata
        {
            Issuer = baseUrl,
            AuthorizationEndpoint = $"{baseUrl}/authorize",
            TokenEndpoint = $"{baseUrl}/token",
            RegistrationEndpoint = $"{baseUrl}/register",
            ResponseTypesSupported = ["code"],
            GrantTypesSupported = ["authorization_code"],
            TokenEndpointAuthMethodsSupported = ["none", "client_secret_post"],
            CodeChallengeMethodsSupported = ["S256"],
            ScopesSupported = ["repo", "read:user"]
        };
    }

    private static async Task<IResult> RegisterClient(
        [FromBody] ClientRegistrationRequest request,
        IOAuthStore store)
    {
        var client = await store.CreateClientAsync(request);

        var response = new ClientRegistrationResponse
        {
            ClientId = client.ClientId,
            ClientSecret = client.ClientSecret,
            ClientName = client.ClientName,
            RedirectUris = client.RedirectUris,
            ClientIdIssuedAt = new DateTimeOffset(client.CreatedAt, TimeSpan.Zero).ToUnixTimeSeconds(),
            ClientSecretExpiresAt = 0
        };

        return Results.Json(response, statusCode: 201);
    }

    private static async Task<IResult> Authorize(
        HttpRequest request,
        IOAuthStore store,
        GitHubOAuthService gitHub)
    {
        var responseType = request.Query["response_type"].ToString();
        var clientId = request.Query["client_id"].ToString();
        var redirectUri = request.Query["redirect_uri"].ToString();
        var codeChallenge = request.Query["code_challenge"].ToString();
        var codeChallengeMethod = request.Query["code_challenge_method"].ToString();
        var state = request.Query["state"].ToString();

        if (responseType != "code")
        {
            return Results.BadRequest(new OAuthError
            {
                Error = "unsupported_response_type",
                ErrorDescription = "Only 'code' response type is supported"
            });
        }

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(redirectUri))
        {
            return Results.BadRequest(new OAuthError
            {
                Error = "invalid_request",
                ErrorDescription = "client_id and redirect_uri are required"
            });
        }

        if (string.IsNullOrEmpty(codeChallenge) || codeChallengeMethod != "S256")
        {
            return Results.BadRequest(new OAuthError
            {
                Error = "invalid_request",
                ErrorDescription = "PKCE with S256 is required"
            });
        }

        var client = await store.GetClientAsync(clientId);
        if (client == null)
        {
            return Results.BadRequest(new OAuthError
            {
                Error = "invalid_client",
                ErrorDescription = "Client not found"
            });
        }

        // Generate state for GitHub OAuth and store the pending authorization
        var gitHubState = GenerateSecureToken();
        await store.StoreGitHubStateAsync(gitHubState, clientId, redirectUri, codeChallenge, codeChallengeMethod);

        // Store the original state from the client so we can return it later
        if (!string.IsNullOrEmpty(state))
        {
            await store.StoreGitHubStateAsync($"origstate:{gitHubState}", clientId, state, "", "");
        }

        // Redirect to GitHub
        var callbackUrl = $"{request.Scheme}://{request.Host}/callback";
        var gitHubAuthUrl = gitHub.GetAuthorizationUrl(gitHubState, callbackUrl);

        return Results.Redirect(gitHubAuthUrl);
    }

    private static async Task<IResult> Callback(
        HttpRequest request,
        IOAuthStore store,
        GitHubOAuthService gitHub)
    {
        var code = request.Query["code"].ToString();
        var state = request.Query["state"].ToString();
        var error = request.Query["error"].ToString();

        if (!string.IsNullOrEmpty(error))
        {
            return Results.BadRequest(new OAuthError
            {
                Error = "access_denied",
                ErrorDescription = request.Query["error_description"].ToString()
            });
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            return Results.BadRequest(new OAuthError
            {
                Error = "invalid_request",
                ErrorDescription = "Missing code or state"
            });
        }

        // Retrieve the stored state
        var storedState = await store.GetGitHubStateAsync(state);
        if (storedState == null)
        {
            return Results.BadRequest(new OAuthError
            {
                Error = "invalid_request",
                ErrorDescription = "Invalid or expired state"
            });
        }

        var (clientId, redirectUri, codeChallenge, codeChallengeMethod) = storedState.Value;

        // Try to get the original client state
        var origStateData = await store.GetGitHubStateAsync($"origstate:{state}");
        var originalState = origStateData?.RedirectUri ?? "";

        // Exchange GitHub code for token
        var gitHubToken = await gitHub.ExchangeCodeForTokenAsync(code);
        if (gitHubToken?.access_token == null)
        {
            return Results.BadRequest(new OAuthError
            {
                Error = "server_error",
                ErrorDescription = "Failed to exchange GitHub code"
            });
        }

        // Get GitHub user info
        var user = await gitHub.GetUserAsync(gitHubToken.access_token);
        if (user == null)
        {
            return Results.BadRequest(new OAuthError
            {
                Error = "server_error",
                ErrorDescription = "Failed to get GitHub user"
            });
        }

        // Create our authorization code
        var authCode = await store.CreateAuthorizationCodeAsync(
            clientId,
            redirectUri,
            codeChallenge,
            codeChallengeMethod,
            $"{user.id}:{gitHubToken.access_token}"
        );

        // Redirect back to the client
        var separator = redirectUri.Contains('?') ? '&' : '?';
        var callbackUrl = $"{redirectUri}{separator}code={authCode.Code}";
        if (!string.IsNullOrEmpty(originalState))
        {
            callbackUrl += $"&state={Uri.EscapeDataString(originalState)}";
        }

        return Results.Redirect(callbackUrl);
    }

    private static async Task<IResult> ExchangeToken(
        HttpRequest request,
        IOAuthStore store)
    {
        var form = await request.ReadFormAsync();

        var grantType = form["grant_type"].ToString();
        var code = form["code"].ToString();
        var redirectUri = form["redirect_uri"].ToString();
        var clientId = form["client_id"].ToString();
        var codeVerifier = form["code_verifier"].ToString();

        if (grantType != "authorization_code")
        {
            return Results.BadRequest(new OAuthError
            {
                Error = "unsupported_grant_type",
                ErrorDescription = "Only authorization_code grant type is supported"
            });
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(codeVerifier))
        {
            return Results.BadRequest(new OAuthError
            {
                Error = "invalid_request",
                ErrorDescription = "code and code_verifier are required"
            });
        }

        // Retrieve and validate the authorization code
        var authCode = await store.GetAndDeleteAuthorizationCodeAsync(code);
        if (authCode == null)
        {
            return Results.BadRequest(new OAuthError
            {
                Error = "invalid_grant",
                ErrorDescription = "Invalid or expired authorization code"
            });
        }

        // Verify PKCE
        if (!VerifyCodeChallenge(codeVerifier, authCode.CodeChallenge))
        {
            return Results.BadRequest(new OAuthError
            {
                Error = "invalid_grant",
                ErrorDescription = "Invalid code_verifier"
            });
        }

        // Verify redirect_uri matches
        if (!string.IsNullOrEmpty(redirectUri) && redirectUri != authCode.RedirectUri)
        {
            return Results.BadRequest(new OAuthError
            {
                Error = "invalid_grant",
                ErrorDescription = "redirect_uri mismatch"
            });
        }

        // Parse the stored GitHub info
        var gitHubParts = authCode.GitHubState?.Split(':', 2);
        if (gitHubParts?.Length != 2)
        {
            return Results.BadRequest(new OAuthError
            {
                Error = "server_error",
                ErrorDescription = "Invalid stored state"
            });
        }

        var gitHubUserId = gitHubParts[0];
        var gitHubAccessToken = gitHubParts[1];

        // Create our token
        var token = await store.CreateTokenAsync(authCode.ClientId, gitHubUserId, gitHubAccessToken);

        return Results.Json(new TokenResponse
        {
            AccessToken = token.AccessToken,
            TokenType = "Bearer",
            ExpiresIn = 3600,
            Scope = "repo read:user"
        });
    }

    private static bool VerifyCodeChallenge(string codeVerifier, string codeChallenge)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.ASCII.GetBytes(codeVerifier));
        var computed = Convert.ToBase64String(hash)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
        return computed == codeChallenge;
    }

    private static string GenerateSecureToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }
}
