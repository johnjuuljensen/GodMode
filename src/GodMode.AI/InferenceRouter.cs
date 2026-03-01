using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace GodMode.AI;

/// <summary>
/// Routes inference requests to the appropriate IChatClient based on tier configuration.
/// Supports both local (ONNX) and remote (Anthropic) providers.
/// </summary>
public sealed class InferenceRouter
{
    private static readonly string[] DefaultFallbackOrder = ["anthropic", "directml", "cpu"];

    private readonly IServiceProvider _services;
    private readonly IHardwareDetector _hardwareDetector;

    private readonly Dictionary<string, IChatClient> _loadedClients = new();
    private Dictionary<InferenceTier, string> _tierProviderMap = new();
    private Dictionary<string, string> _providerStatus = new();
    private AIConfig _config = new();

    public event EventHandler? StatusChanged;

    /// <summary>Current tier-to-provider mapping (for UI display).</summary>
    public IReadOnlyDictionary<InferenceTier, string> TierProviderMap => _tierProviderMap;

    /// <summary>Per-provider status (for UI display): "loaded", "unavailable", "failed", etc.</summary>
    public IReadOnlyDictionary<string, string> ProviderStatus => _providerStatus;

    /// <summary>True when at least one client is loaded and ready.</summary>
    public bool IsLoaded => _loadedClients.Count > 0;

    /// <summary>Provider used for the most recent GenerateAsync call (for logging/diagnostics).</summary>
    public string? LastUsedProvider { get; private set; }

    /// <summary>Tier requested for the most recent GenerateAsync call.</summary>
    public InferenceTier? LastUsedTier { get; private set; }

    public InferenceRouter(IServiceProvider services, IHardwareDetector hardwareDetector)
    {
        _services = services;
        _hardwareDetector = hardwareDetector;
    }

    /// <summary>
    /// Initialize the router from AIConfig: build tier-to-provider mapping, create clients.
    /// </summary>
    public async Task InitializeAsync()
    {
        _config = AIConfig.Load();
        var available = _hardwareDetector.DetectAvailableProviders();

        if (_config.Tiers is { Count: > 0 })
            BuildFromExplicitConfig(_config, available);
        else
            BuildFromAutoDetect(_config, available);

        await LoadClientsAsync(_config);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Backward-compat: stores the path and delegates to parameterless init.
    /// </summary>
    public async Task InitializeAsync(string modelPath)
    {
        var config = AIConfig.Load();
        if (!string.IsNullOrEmpty(modelPath))
        {
            if (File.Exists(Path.Combine(modelPath, "genai_config.json")))
                config.ModelPath = modelPath;

            config.Save();
        }

        await InitializeAsync();
    }

    /// <summary>
    /// Generate a response using a specific inference tier with fallback.
    /// </summary>
    public async Task<string> GenerateAsync(InferenceTier tier, string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        LastUsedTier = tier;

        // Try the configured provider for this tier
        if (_tierProviderMap.TryGetValue(tier, out var provider) &&
            _loadedClients.TryGetValue(provider, out var client))
        {
            LastUsedProvider = provider;
            return await CallClientAsync(client, provider, systemPrompt, userMessage, ct);
        }

        // Fallback chain
        foreach (var fallback in DefaultFallbackOrder)
        {
            if (_loadedClients.TryGetValue(fallback, out var fbClient))
            {
                LastUsedProvider = fallback;
                return await CallClientAsync(fbClient, fallback, systemPrompt, userMessage, ct);
            }
        }

        // Try any loaded client regardless of tier
        foreach (var (key, c) in _loadedClients)
        {
            LastUsedProvider = key;
            return await CallClientAsync(c, key, systemPrompt, userMessage, ct);
        }

        // Nothing loaded
        LastUsedProvider = "none";
        return string.Empty;
    }

    private async Task<string> CallClientAsync(IChatClient client, string provider, string systemPrompt, string userMessage, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userMessage)
        };

        var options = new ChatOptions
        {
            MaxOutputTokens = _config.MaxTokens,
            Temperature = (float)_config.Temperature,
        };

        // Local ONNX models benefit from JSON response format; remote models don't support it this way
        if (provider is "directml" or "cpu")
        {
            options.TopP = 0.9f;
            options.ResponseFormat = ChatResponseFormatJson.Json;
        }

        var response = await client.GetResponseAsync(messages, options, ct);
        return response.Text ?? string.Empty;
    }

    private void BuildFromExplicitConfig(AIConfig config, IReadOnlySet<string> available)
    {
        _tierProviderMap = new Dictionary<InferenceTier, string>();
        foreach (var (tier, tierConfig) in config.Tiers!)
        {
            var provider = tierConfig.Provider.ToLowerInvariant();
            if (provider == "auto")
                provider = PickBestProvider(tier, available, config);

            _tierProviderMap[tier] = provider;
        }

        foreach (var tier in Enum.GetValues<InferenceTier>())
        {
            if (!_tierProviderMap.ContainsKey(tier))
                _tierProviderMap[tier] = PickBestProvider(tier, available, config);
        }
    }

    private void BuildFromAutoDetect(AIConfig config, IReadOnlySet<string> available)
    {
        _tierProviderMap = new Dictionary<InferenceTier, string>();

        var useAnthropicProvider = config.Provider is null or "anthropic" or "auto";
        var hasAnthropicReady = HasAnthropicKey(config) && useAnthropicProvider;

        var hasDirectMl = !string.IsNullOrEmpty(config.ModelPath) && available.Contains("directml");
        var hasCpu = available.Contains("cpu") && !string.IsNullOrEmpty(config.ModelPath);

        if (hasAnthropicReady)
        {
            _tierProviderMap[InferenceTier.Light] = "anthropic";
            _tierProviderMap[InferenceTier.Medium] = "anthropic";
            _tierProviderMap[InferenceTier.Heavy] = "anthropic";
        }
        else if (hasDirectMl)
        {
            _tierProviderMap[InferenceTier.Light] = "directml";
            _tierProviderMap[InferenceTier.Medium] = "directml";
            _tierProviderMap[InferenceTier.Heavy] = "directml";
        }
        else if (hasCpu)
        {
            _tierProviderMap[InferenceTier.Light] = "cpu";
            _tierProviderMap[InferenceTier.Medium] = "cpu";
            _tierProviderMap[InferenceTier.Heavy] = "cpu";
        }
        else
        {
            _tierProviderMap[InferenceTier.Light] = "none";
            _tierProviderMap[InferenceTier.Medium] = "none";
            _tierProviderMap[InferenceTier.Heavy] = "none";
        }
    }

    private static bool HasAnthropicKey(AIConfig config) =>
        !string.IsNullOrEmpty(config.ApiKey)
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

    private static string PickBestProvider(InferenceTier tier, IReadOnlySet<string> available, AIConfig config)
    {
        if (HasAnthropicKey(config))
            return "anthropic";
        if (available.Contains("directml") && !string.IsNullOrEmpty(config.ModelPath))
            return "directml";
        if (available.Contains("cpu") && !string.IsNullOrEmpty(config.ModelPath))
            return "cpu";
        return "none";
    }

    private async Task LoadClientsAsync(AIConfig config)
    {
        _providerStatus = new Dictionary<string, string>();

        var providersNeeded = _tierProviderMap.Values.Distinct().Where(p => p != "none").ToList();

        foreach (var provider in providersNeeded)
        {
            if (_loadedClients.ContainsKey(provider))
            {
                _providerStatus[provider] = "loaded";
                continue;
            }

            try
            {
                var factory = _services.GetKeyedService<IChatClientFactory>(provider);
                if (factory is null)
                {
                    _providerStatus[provider] = "unavailable";
                    continue;
                }

                var client = await factory.CreateAsync(config);
                _loadedClients[provider] = client;
                _providerStatus[provider] = "loaded";
            }
            catch (Exception ex)
            {
                _providerStatus[provider] = $"failed: {ex.Message}";
            }
        }

        _providerStatus["none"] = "no provider";
    }
}
