using Microsoft.Extensions.DependencyInjection;

namespace GodMode.AI;

/// <summary>
/// Routes inference requests to the appropriate model based on tier configuration.
/// Implements ILanguageModel for backward compatibility (defaults to Medium tier).
/// </summary>
public sealed class InferenceRouter : ILanguageModel
{
    private static readonly string[] DefaultFallbackOrder = ["npu", "directml", "cpu"];

    private readonly IServiceProvider _services;
    private readonly IHardwareDetector _hardwareDetector;

    private readonly Dictionary<string, ILanguageModel> _loadedModels = new();
    private Dictionary<InferenceTier, string> _tierProviderMap = new();
    private Dictionary<string, string> _providerStatus = new();

    public event EventHandler? StatusChanged;

    /// <summary>Current tier-to-provider mapping (for UI display).</summary>
    public IReadOnlyDictionary<InferenceTier, string> TierProviderMap => _tierProviderMap;

    /// <summary>Per-provider status (for UI display): "loaded", "unavailable", "failed", etc.</summary>
    public IReadOnlyDictionary<string, string> ProviderStatus => _providerStatus;

    /// <summary>True when at least one model is loaded and ready.</summary>
    public bool IsLoaded => _loadedModels.Values.Any(m => m.IsLoaded);

    public InferenceRouter(IServiceProvider services, IHardwareDetector hardwareDetector)
    {
        _services = services;
        _hardwareDetector = hardwareDetector;
    }

    /// <summary>
    /// Initialize the router from AIConfig: build tier→provider mapping, load models.
    /// </summary>
    public async Task InitializeAsync()
    {
        var config = AIConfig.Load();
        var available = _hardwareDetector.DetectAvailableProviders();

        if (config.Tiers is { Count: > 0 })
            BuildFromExplicitConfig(config, available);
        else
            BuildFromAutoDetect(config, available);

        await LoadModelsAsync(config);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Backward-compat: InitializeAsync(string modelPath) stores the path and delegates to parameterless init.
    /// </summary>
    public async Task InitializeAsync(string modelPath)
    {
        // Legacy callers pass a single model path — store it so auto-detect picks it up
        var config = AIConfig.Load();
        if (!string.IsNullOrEmpty(modelPath))
        {
            // Determine if this is an NPU or DirectML model by checking for marker files
            if (File.Exists(Path.Combine(modelPath, "genai_config.json")))
                config.ModelPath = modelPath;
            else if (File.Exists(Path.Combine(modelPath, "tokenizer.json")))
                config.NpuModelPath = modelPath;

            config.Save();
        }

        await InitializeAsync();
    }

    /// <summary>
    /// Generate a response using a specific inference tier with fallback.
    /// </summary>
    public async Task<string> GenerateAsync(InferenceTier tier, string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        // Try the configured provider for this tier
        if (_tierProviderMap.TryGetValue(tier, out var provider) &&
            _loadedModels.TryGetValue(provider, out var model) && model.IsLoaded)
        {
            return await model.GenerateAsync(systemPrompt, userMessage, ct);
        }

        // Fallback chain
        foreach (var fallback in DefaultFallbackOrder)
        {
            if (_loadedModels.TryGetValue(fallback, out var fbModel) && fbModel.IsLoaded)
                return await fbModel.GenerateAsync(systemPrompt, userMessage, ct);
        }

        // Try any loaded model regardless of tier
        var anyLoaded = _loadedModels.Values.FirstOrDefault(m => m.IsLoaded);
        if (anyLoaded is not null)
            return await anyLoaded.GenerateAsync(systemPrompt, userMessage, ct);

        // Nothing loaded — NullLanguageModel behavior
        return string.Empty;
    }

    /// <summary>
    /// ILanguageModel contract — delegates to Medium tier.
    /// </summary>
    public Task<string> GenerateAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
        => GenerateAsync(InferenceTier.Medium, systemPrompt, userMessage, ct);

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

        // Fill any unconfigured tiers with auto-detected providers
        foreach (var tier in Enum.GetValues<InferenceTier>())
        {
            if (!_tierProviderMap.ContainsKey(tier))
                _tierProviderMap[tier] = PickBestProvider(tier, available, config);
        }
    }

    private void BuildFromAutoDetect(AIConfig config, IReadOnlySet<string> available)
    {
        _tierProviderMap = new Dictionary<InferenceTier, string>();

        var hasNpu = !string.IsNullOrEmpty(config.NpuModelPath) && available.Contains("npu");
        var hasDirectMl = !string.IsNullOrEmpty(config.ModelPath) && available.Contains("directml");
        var hasCpu = available.Contains("cpu") && !string.IsNullOrEmpty(config.ModelPath);

        if (hasNpu && hasDirectMl)
        {
            _tierProviderMap[InferenceTier.Light] = "npu";
            _tierProviderMap[InferenceTier.Medium] = "directml";
            _tierProviderMap[InferenceTier.Heavy] = "directml";
        }
        else if (hasNpu)
        {
            _tierProviderMap[InferenceTier.Light] = "npu";
            _tierProviderMap[InferenceTier.Medium] = "npu";
            _tierProviderMap[InferenceTier.Heavy] = "npu";
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

    private static string PickBestProvider(InferenceTier tier, IReadOnlySet<string> available, AIConfig config)
    {
        if (tier == InferenceTier.Light && available.Contains("npu") && !string.IsNullOrEmpty(config.NpuModelPath))
            return "npu";
        if (available.Contains("directml") && !string.IsNullOrEmpty(config.ModelPath))
            return "directml";
        if (available.Contains("cpu") && !string.IsNullOrEmpty(config.ModelPath))
            return "cpu";
        return "none";
    }

    private async Task LoadModelsAsync(AIConfig config)
    {
        _providerStatus = new Dictionary<string, string>();

        // Collect unique providers that need models
        var providersNeeded = _tierProviderMap.Values.Distinct().Where(p => p != "none").ToList();

        foreach (var provider in providersNeeded)
        {
            if (_loadedModels.ContainsKey(provider))
            {
                // Already loaded (shared instance)
                _providerStatus[provider] = _loadedModels[provider].IsLoaded ? "loaded" : "failed";
                continue;
            }

            var model = _services.GetKeyedService<ILanguageModel>(provider);
            if (model is null)
            {
                _providerStatus[provider] = "unavailable";
                continue;
            }

            var modelPath = ResolveModelPath(provider, config);
            if (modelPath is null)
            {
                _providerStatus[provider] = "no model path";
                continue;
            }

            try
            {
                await model.InitializeAsync(modelPath);
                _loadedModels[provider] = model;
                _providerStatus[provider] = model.IsLoaded ? "loaded" : "failed";
            }
            catch (Exception ex)
            {
                _providerStatus[provider] = $"failed: {ex.Message}";
                _loadedModels[provider] = model; // Keep reference so fallback can check IsLoaded
            }
        }

        _providerStatus["none"] = "no model";
    }

    private static string? ResolveModelPath(string provider, AIConfig config)
    {
        // Check explicit tier config first
        if (config.Tiers is { Count: > 0 })
        {
            var tierWithPath = config.Tiers.Values
                .FirstOrDefault(t => t.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase) && t.ModelPath is not null);
            if (tierWithPath?.ModelPath is not null)
                return tierWithPath.ModelPath;
        }

        return provider switch
        {
            "npu" => config.NpuModelPath,
            "directml" => config.ModelPath,
            "cpu" => config.ModelPath,
            _ => null
        };
    }
}
