using System.Security.Cryptography;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace GodMode.Mcp.OAuth;

public class DynamoDbOAuthStore : IOAuthStore
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public DynamoDbOAuthStore(IAmazonDynamoDB dynamoDb, IConfiguration configuration)
    {
        _dynamoDb = dynamoDb;
        _tableName = configuration["DynamoDB:TableName"] ?? "GodModeMcp";
    }

    public async Task<OAuthClient> CreateClientAsync(ClientRegistrationRequest request)
    {
        var clientId = GenerateSecureToken();
        var clientSecret = GenerateSecureToken();
        var now = DateTime.UtcNow;

        var client = new OAuthClient(
            clientId,
            clientSecret,
            request.ClientName ?? "Unknown",
            request.RedirectUris ?? [],
            now
        );

        await _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"CLIENT#{clientId}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["ClientSecret"] = new AttributeValue { S = clientSecret },
                ["ClientName"] = new AttributeValue { S = client.ClientName },
                ["RedirectUris"] = new AttributeValue { SS = client.RedirectUris.Count > 0 ? client.RedirectUris : ["none"] },
                ["CreatedAt"] = new AttributeValue { S = now.ToString("O") },
                ["TTL"] = new AttributeValue { N = now.AddDays(30).ToUnixTimeSeconds().ToString() }
            }
        });

        return client;
    }

    public async Task<OAuthClient?> GetClientAsync(string clientId)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"CLIENT#{clientId}" },
                ["SK"] = new AttributeValue { S = "META" }
            }
        });

        if (!response.IsItemSet)
            return null;

        var item = response.Item;
        return new OAuthClient(
            clientId,
            item.TryGetValue("ClientSecret", out var secret) ? secret.S : null,
            item["ClientName"].S,
            item["RedirectUris"].SS.Where(s => s != "none").ToList(),
            DateTime.Parse(item["CreatedAt"].S)
        );
    }

    public async Task<AuthorizationCode> CreateAuthorizationCodeAsync(
        string clientId,
        string redirectUri,
        string codeChallenge,
        string codeChallengeMethod,
        string? gitHubState)
    {
        var code = GenerateSecureToken();
        var expiresAt = DateTime.UtcNow.AddMinutes(10);

        var authCode = new AuthorizationCode(
            code,
            clientId,
            redirectUri,
            codeChallenge,
            codeChallengeMethod,
            gitHubState,
            expiresAt
        );

        await _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"AUTHCODE#{code}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["ClientId"] = new AttributeValue { S = clientId },
                ["RedirectUri"] = new AttributeValue { S = redirectUri },
                ["CodeChallenge"] = new AttributeValue { S = codeChallenge },
                ["CodeChallengeMethod"] = new AttributeValue { S = codeChallengeMethod },
                ["GitHubState"] = new AttributeValue { S = gitHubState ?? "" },
                ["ExpiresAt"] = new AttributeValue { S = expiresAt.ToString("O") },
                ["TTL"] = new AttributeValue { N = expiresAt.ToUnixTimeSeconds().ToString() }
            }
        });

        return authCode;
    }

    public async Task<AuthorizationCode?> GetAndDeleteAuthorizationCodeAsync(string code)
    {
        var response = await _dynamoDb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"AUTHCODE#{code}" },
                ["SK"] = new AttributeValue { S = "META" }
            },
            ReturnValues = ReturnValue.ALL_OLD
        });

        if (response.Attributes.Count == 0)
            return null;

        var item = response.Attributes;
        var authCode = new AuthorizationCode(
            code,
            item["ClientId"].S,
            item["RedirectUri"].S,
            item["CodeChallenge"].S,
            item["CodeChallengeMethod"].S,
            string.IsNullOrEmpty(item["GitHubState"].S) ? null : item["GitHubState"].S,
            DateTime.Parse(item["ExpiresAt"].S)
        );

        if (authCode.ExpiresAt < DateTime.UtcNow)
            return null;

        return authCode;
    }

    public async Task<TokenRecord> CreateTokenAsync(string clientId, string gitHubUserId, string gitHubAccessToken)
    {
        var accessToken = GenerateSecureToken();
        var refreshToken = GenerateSecureToken();
        var expiresAt = DateTime.UtcNow.AddHours(1);

        var token = new TokenRecord(accessToken, refreshToken, clientId, gitHubUserId, gitHubAccessToken, expiresAt);

        // Store access token
        await _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"TOKEN#{accessToken}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["RefreshToken"] = new AttributeValue { S = refreshToken },
                ["ClientId"] = new AttributeValue { S = clientId },
                ["GitHubUserId"] = new AttributeValue { S = gitHubUserId },
                ["GitHubAccessToken"] = new AttributeValue { S = gitHubAccessToken },
                ["ExpiresAt"] = new AttributeValue { S = expiresAt.ToString("O") },
                ["TTL"] = new AttributeValue { N = expiresAt.AddDays(30).ToUnixTimeSeconds().ToString() }
            }
        });

        // Store refresh token mapping (long-lived, 30 days)
        await _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"REFRESH#{refreshToken}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["AccessToken"] = new AttributeValue { S = accessToken },
                ["TTL"] = new AttributeValue { N = DateTime.UtcNow.AddDays(30).ToUnixTimeSeconds().ToString() }
            }
        });

        return token;
    }

    public async Task<TokenRecord?> GetTokenAsync(string accessToken)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"TOKEN#{accessToken}" },
                ["SK"] = new AttributeValue { S = "META" }
            }
        });

        if (!response.IsItemSet)
            return null;

        var item = response.Item;
        var token = new TokenRecord(
            accessToken,
            item["RefreshToken"].S,
            item["ClientId"].S,
            item["GitHubUserId"].S,
            item["GitHubAccessToken"].S,
            DateTime.Parse(item["ExpiresAt"].S)
        );

        if (token.ExpiresAt < DateTime.UtcNow)
            return null;

        return token;
    }

    public async Task<TokenRecord?> GetTokenByRefreshTokenAsync(string refreshToken)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"REFRESH#{refreshToken}" },
                ["SK"] = new AttributeValue { S = "META" }
            }
        });

        if (!response.IsItemSet)
            return null;

        var accessToken = response.Item["AccessToken"].S;

        // Get the full token record
        var tokenResponse = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"TOKEN#{accessToken}" },
                ["SK"] = new AttributeValue { S = "META" }
            }
        });

        if (!tokenResponse.IsItemSet)
            return null;

        var item = tokenResponse.Item;
        return new TokenRecord(
            accessToken,
            item["RefreshToken"].S,
            item["ClientId"].S,
            item["GitHubUserId"].S,
            item["GitHubAccessToken"].S,
            DateTime.Parse(item["ExpiresAt"].S)
        );
    }

    public async Task DeleteTokenAsync(string accessToken)
    {
        // Get the token first to find the refresh token
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"TOKEN#{accessToken}" },
                ["SK"] = new AttributeValue { S = "META" }
            }
        });

        if (response.IsItemSet)
        {
            var refreshToken = response.Item["RefreshToken"].S;

            // Delete refresh token mapping
            await _dynamoDb.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = $"REFRESH#{refreshToken}" },
                    ["SK"] = new AttributeValue { S = "META" }
                }
            });
        }

        // Delete access token
        await _dynamoDb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"TOKEN#{accessToken}" },
                ["SK"] = new AttributeValue { S = "META" }
            }
        });
    }

    public async Task StoreGitHubStateAsync(
        string state,
        string clientId,
        string redirectUri,
        string codeChallenge,
        string codeChallengeMethod)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(10);

        await _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"GHSTATE#{state}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["ClientId"] = new AttributeValue { S = clientId },
                ["RedirectUri"] = new AttributeValue { S = redirectUri },
                ["CodeChallenge"] = new AttributeValue { S = codeChallenge },
                ["CodeChallengeMethod"] = new AttributeValue { S = codeChallengeMethod },
                ["TTL"] = new AttributeValue { N = expiresAt.ToUnixTimeSeconds().ToString() }
            }
        });
    }

    public async Task<(string ClientId, string RedirectUri, string CodeChallenge, string CodeChallengeMethod)?> GetGitHubStateAsync(string state)
    {
        var response = await _dynamoDb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"GHSTATE#{state}" },
                ["SK"] = new AttributeValue { S = "META" }
            },
            ReturnValues = ReturnValue.ALL_OLD
        });

        if (response.Attributes.Count == 0)
            return null;

        var item = response.Attributes;
        return (
            item["ClientId"].S,
            item["RedirectUri"].S,
            item["CodeChallenge"].S,
            item["CodeChallengeMethod"].S
        );
    }

    private static string GenerateSecureToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }
}

public static class DateTimeExtensions
{
    public static long ToUnixTimeSeconds(this DateTime dateTime)
    {
        return new DateTimeOffset(dateTime, TimeSpan.Zero).ToUnixTimeSeconds();
    }
}
