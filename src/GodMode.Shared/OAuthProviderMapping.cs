namespace GodMode.Shared;

/// <summary>
/// Maps OAuth providers to the MCP connector IDs they authenticate.
/// </summary>
public static class OAuthProviderMapping
{
    public static readonly IReadOnlyDictionary<string, string[]> ProviderToConnectors =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["microsoft"] = ["azure"],
        };

    public static readonly IReadOnlyDictionary<string, string> ConnectorToProvider =
        ProviderToConnectors
            .SelectMany(kvp => kvp.Value.Select(c => (Connector: c, Provider: kvp.Key)))
            .ToDictionary(t => t.Connector, t => t.Provider, StringComparer.OrdinalIgnoreCase);

    public static readonly string[] SupportedProviders = ["microsoft"];

    public static bool IsSupported(string provider) =>
        SupportedProviders.Contains(provider, StringComparer.OrdinalIgnoreCase);
}
