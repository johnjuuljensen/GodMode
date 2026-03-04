using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Reads root configuration using multi-file discovery and merging.
/// Scans .godmode-root/ for config.json (base) and config.*.json (per-action overlays).
/// Schema files are discovered by convention: {actionName}/schema.json.
/// No caching — always reads fresh so changes take effect without restart.
/// </summary>
public class RootConfigReader : IRootConfigReader
{
    private const string GodModeRootDir = ".godmode-root";
    private const string BaseConfigFileName = "config.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>
    /// Default input schema with name and prompt fields.
    /// Used when no schema.json file exists for an action.
    /// </summary>
    private static readonly JsonElement DefaultSchema = JsonSerializer.Deserialize<JsonElement>("""
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "title": "Project Name" },
            "prompt": { "type": "string", "title": "Task Description", "x-multiline": true },
            "skipPermissions": { "type": "boolean", "title": "Skip Permissions", "description": "Start Claude with --dangerously-skip-permissions", "default": "true" }
          },
          "required": ["name", "prompt"]
        }
        """);

    private readonly ILogger<RootConfigReader> _logger;

    public RootConfigReader(ILogger<RootConfigReader> logger)
    {
        _logger = logger;
    }

    public RootConfig ReadConfig(string rootPath)
    {
        var godModeRootPath = Path.Combine(rootPath, GodModeRootDir);
        var baseConfigPath = Path.Combine(godModeRootPath, BaseConfigFileName);

        if (!File.Exists(baseConfigPath))
        {
            _logger.LogDebug("No config found in {RootPath}, using default config", rootPath);
            return BuildDefaultConfig();
        }

        try
        {
            var baseRaw = ReadRawConfig(baseConfigPath);
            var actionOverlays = DiscoverActionConfigs(godModeRootPath);

            Dictionary<string, CreateAction> actions;

            if (actionOverlays.Count == 0)
            {
                // config.json is the single action (named "Create")
                var merged = baseRaw;
                var schema = LoadSchema(godModeRootPath, null);
                var action = BuildAction("Create", merged, godModeRootPath, schema);
                actions = new Dictionary<string, CreateAction>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Create"] = action
                };
            }
            else
            {
                actions = new Dictionary<string, CreateAction>(StringComparer.OrdinalIgnoreCase);
                foreach (var (actionName, overlayRaw) in actionOverlays)
                {
                    var merged = MergeRawConfigs(baseRaw, overlayRaw);
                    var schema = LoadSchema(godModeRootPath, actionName);
                    var action = BuildAction(actionName, merged, godModeRootPath, schema);
                    actions[actionName] = action;
                }
            }

            return new RootConfig(
                Description: baseRaw.Description,
                Actions: actions,
                ProfileName: baseRaw.ProfileName,
                StripEnvVarProfile: baseRaw.StripEnvVarProfile ?? false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read config from {ConfigPath}, using default config", baseConfigPath);
            return BuildDefaultConfig();
        }
    }

    private static RootConfig BuildDefaultConfig() =>
        new(Actions: new Dictionary<string, CreateAction>(StringComparer.OrdinalIgnoreCase)
        {
            ["Create"] = new("Create", InputSchema: DefaultSchema)
        });

    /// <summary>
    /// Discovers config.*.json files and returns a dictionary of actionName → RawConfig.
    /// </summary>
    private Dictionary<string, RawConfig> DiscoverActionConfigs(string godModeRootPath)
    {
        var result = new Dictionary<string, RawConfig>(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in Directory.GetFiles(godModeRootPath, "config.*.json"))
        {
            var fileName = Path.GetFileName(filePath);
            // Extract action name: config.{name}.json
            var actionName = fileName["config.".Length..^".json".Length];
            if (string.IsNullOrEmpty(actionName)) continue;

            try
            {
                var raw = ReadRawConfig(filePath);
                result[actionName] = raw;
                _logger.LogDebug("Discovered action config '{ActionName}' from {FilePath}", actionName, filePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read action config from {FilePath}, skipping", filePath);
            }
        }

        return result;
    }

    private static RawConfig ReadRawConfig(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<RawConfig>(json, JsonOptions) ?? new RawConfig();
    }

    /// <summary>
    /// Merges base config with an action overlay.
    /// Rules: scalars — overlay replaces; dictionaries — merge (overlay wins); claudeArgs — concatenate.
    /// </summary>
    private static RawConfig MergeRawConfigs(RawConfig baseConfig, RawConfig overlay) => new()
    {
        Description = overlay.Description ?? baseConfig.Description,
        Prepare = overlay.Prepare ?? baseConfig.Prepare,
        Create = overlay.Create ?? baseConfig.Create,
        Delete = overlay.Delete ?? baseConfig.Delete,
        Environment = MergeDictionaries(baseConfig.Environment, overlay.Environment),
        ClaudeArgs = ConcatArrays(baseConfig.ClaudeArgs, overlay.ClaudeArgs),
        NameTemplate = overlay.NameTemplate ?? baseConfig.NameTemplate,
        PromptTemplate = overlay.PromptTemplate ?? baseConfig.PromptTemplate,
        ScriptsCreateFolder = overlay.ScriptsCreateFolder ?? baseConfig.ScriptsCreateFolder
    };

    /// <summary>
    /// Builds a resolved CreateAction from a (merged) RawConfig.
    /// Normalizes script fields and prepends .godmode-root/ to make paths rootPath-relative.
    /// </summary>
    private CreateAction BuildAction(string name, RawConfig raw, string godModeRootPath, JsonElement? schema)
    {
        return new CreateAction(
            Name: name,
            Description: raw.Description,
            InputSchema: schema,
            Prepare: NormalizeScriptPaths(raw.Prepare, godModeRootPath),
            Create: NormalizeScriptPaths(raw.Create, godModeRootPath),
            Delete: NormalizeScriptPaths(raw.Delete, godModeRootPath),
            Environment: raw.Environment,
            ClaudeArgs: raw.ClaudeArgs,
            NameTemplate: raw.NameTemplate,
            PromptTemplate: raw.PromptTemplate,
            ScriptsCreateFolder: raw.ScriptsCreateFolder ?? false
        );
    }

    /// <summary>
    /// Loads {actionName}/schema.json from .godmode-root/ if it exists.
    /// Falls back to the default schema (name + prompt) if not found.
    /// When actionName is null (single-action mode), looks for schema.json directly in .godmode-root/.
    /// </summary>
    private JsonElement? LoadSchema(string godModeRootPath, string? actionName)
    {
        var schemaPath = actionName != null
            ? Path.Combine(godModeRootPath, actionName, "schema.json")
            : Path.Combine(godModeRootPath, "schema.json");

        if (!File.Exists(schemaPath))
        {
            _logger.LogDebug("No schema file at {SchemaPath}, using default schema", schemaPath);
            return DefaultSchema;
        }

        try
        {
            var json = File.ReadAllText(schemaPath);
            return JsonSerializer.Deserialize<JsonElement>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read schema from {SchemaPath}, using default schema", schemaPath);
            return DefaultSchema;
        }
    }

    /// <summary>
    /// Normalizes a script JsonElement (string or string[]) to string[],
    /// and prepends .godmode-root/ to each path to make them rootPath-relative for ScriptRunner.
    /// </summary>
    private static string[]? NormalizeScriptPaths(JsonElement? element, string godModeRootPath)
    {
        if (element is not { } el) return null;

        var paths = el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() is { } s ? [s] : null,
            JsonValueKind.Array => el.EnumerateArray()
                .Select(e => e.GetString())
                .OfType<string>()
                .ToArray(),
            _ => null
        };

        if (paths is not { Length: > 0 }) return null;

        // Prepend .godmode-root/ to make paths rootPath-relative
        return paths.Select(p => Path.Combine(GodModeRootDir, p)).ToArray();
    }

    private static Dictionary<string, string>? MergeDictionaries(
        Dictionary<string, string>? baseDict, Dictionary<string, string>? overrideDict)
    {
        if (baseDict == null) return overrideDict;
        if (overrideDict == null) return baseDict;

        var merged = new Dictionary<string, string>(baseDict);
        foreach (var (key, value) in overrideDict)
            merged[key] = value;
        return merged;
    }

    private static string[]? ConcatArrays(string[]? baseArr, string[]? additionalArr)
    {
        if (baseArr == null) return additionalArr;
        if (additionalArr == null) return baseArr;
        return [.. baseArr, .. additionalArr];
    }

    /// <summary>
    /// Raw deserialization type for config files. All fields nullable.
    /// Script fields use JsonElement to accept both string and string[] from JSON.
    /// </summary>
    private record RawConfig
    {
        public string? Description { get; init; }
        public string? ProfileName { get; init; }
        public JsonElement? Prepare { get; init; }
        public JsonElement? Create { get; init; }
        public JsonElement? Delete { get; init; }
        public Dictionary<string, string>? Environment { get; init; }
        public string[]? ClaudeArgs { get; init; }
        public string? NameTemplate { get; init; }
        public string? PromptTemplate { get; init; }
        public bool? ScriptsCreateFolder { get; init; }
        public bool? StripEnvVarProfile { get; init; }
    }
}
