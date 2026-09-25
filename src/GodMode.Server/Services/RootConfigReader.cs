using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using GodMode.ProjectFiles;
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
    private const string GodModeRootDir = ProjectFolder.RootConfigFolderName;
    private const string BaseConfigFileName = "config.json";

    /// <summary>The key a root or action config carried MCP servers under. GodMode ignores it, and says so once.</summary>
    private const string IgnoredMcpServersKey = "mcpServers";

    /// <summary>Where a session's MCP servers come from, for the warning about MCP config GodMode ignores.</summary>
    internal const string McpServersHint = "a repo brings its MCP servers in its own .mcp.json, and user-scoped ones live in the profile's CLAUDE_CONFIG_DIR";

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
            "skipPermissions": { "type": "boolean", "title": "Skip Permissions", "description": "Start Claude with --dangerously-skip-permissions; otherwise tool calls that need approval wait for you", "default": false }
          },
          "required": ["name", "prompt"]
        }
        """);

    private readonly ILogger<RootConfigReader> _logger;

    /// <summary>The config files whose MCP servers have been logged as ignored: it is said once, not on every read.</summary>
    private readonly ConcurrentDictionary<string, byte> _ignoredMcpLogged = new(StringComparer.OrdinalIgnoreCase);

    public RootConfigReader(ILogger<RootConfigReader> logger)
    {
        _logger = logger;
    }

    public RootConfig ReadConfig(string rootPath)
    {
        try
        {
            return Read(rootPath, strict: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read config from {RootPath}, using default config", rootPath);
            return BuildDefaultConfig();
        }
    }

    public RootConfig ReadConfigStrict(string rootPath) => Read(rootPath, strict: true);

    /// <param name="strict">An action overlay that cannot be read throws, rather than being skipped.</param>
    private RootConfig Read(string rootPath, bool strict)
    {
        var godModeRootPath = Path.Combine(rootPath, GodModeRootDir);
        var baseConfigPath = Path.Combine(godModeRootPath, BaseConfigFileName);

        if (!File.Exists(baseConfigPath))
        {
            _logger.LogDebug("No config found in {RootPath}, using default config", rootPath);
            return BuildDefaultConfig();
        }

        var baseRaw = ReadRawConfig(baseConfigPath);
        var actionOverlays = DiscoverActionConfigs(godModeRootPath, strict);

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

    private static RootConfig BuildDefaultConfig() =>
        new(Actions: new Dictionary<string, CreateAction>(StringComparer.OrdinalIgnoreCase)
        {
            ["Create"] = new("Create", InputSchema: DefaultSchema)
        });

    /// <summary>
    /// Discovers config.*.json files and returns a dictionary of actionName → RawConfig.
    /// </summary>
    private Dictionary<string, RawConfig> DiscoverActionConfigs(string godModeRootPath, bool strict)
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
            catch (Exception ex) when (!strict)
            {
                _logger.LogWarning(ex, "Failed to read action config from {FilePath}, skipping", filePath);
            }
        }

        return result;
    }

    private RawConfig ReadRawConfig(string path)
    {
        var raw = ReadSettled(path, json => JsonSerializer.Deserialize<RawConfig>(json, JsonOptions)) ?? new RawConfig();
        // A mode claude does not have, or bypassPermissions, is an error in the file that names it
        if (raw.PermissionMode is { } mode)
            raw = raw with { PermissionMode = PermissionModes.Canonical(mode) ?? throw new InvalidDataException($"{Path.GetFileName(path)}: {PermissionModes.Refusal(mode)}") };
        // GodMode gives a session no MCP server but its own: a leftover mcpServers is not an error
        if (raw.Unrecognized?.Keys.Any(key => key.Equals(IgnoredMcpServersKey, StringComparison.OrdinalIgnoreCase)) == true
            && _ignoredMcpLogged.TryAdd(Path.GetFullPath(path), 0))
            _logger.LogWarning("{ConfigPath} has " + IgnoredMcpServersKey + ", which GodMode ignores: {Hint}", path, McpServersHint);
        return raw;
    }

    /// <summary>
    /// Parses a root file the host may be saving as it is read, by hand in an editor or by a script.
    /// It is opened so that the save is not refused (a plain read shares no writing, and on Windows a
    /// save during one fails), and a read that does not parse is read again while the file is still
    /// changing, so a file half saved is not taken for a broken one. One that does not parse and has
    /// not changed for <see cref="SettleTime"/> is broken, as is one still changing after <see cref="MaxSettleTime"/>.
    /// </summary>
    private static T ReadSettled<T>(string path, Func<string, T> parse)
    {
        var reading = Stopwatch.StartNew();
        var unchanged = Stopwatch.StartNew();
        string? previous = null;
        while (true)
        {
            var text = ReadShared(path);
            if (text != previous) { previous = text; unchanged.Restart(); }
            try { return parse(text); }
            catch (JsonException) when (unchanged.Elapsed < SettleTime && reading.Elapsed < MaxSettleTime)
            {
                Thread.Sleep(RetryDelay);
            }
        }
    }

    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MaxSettleTime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);
    private const int OpenAttempts = 20;

    /// <summary>
    /// Reads the file sharing everything, so a write, a delete or a replace is not refused while it
    /// is open. A save that shares nothing while it writes is waited for.
    /// </summary>
    private static string ReadShared(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < OpenAttempts && File.Exists(path))
            {
                Thread.Sleep(RetryDelay);
            }
        }
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
        Status = overlay.Status ?? baseConfig.Status,
        ResumeOnRestart = overlay.ResumeOnRestart ?? baseConfig.ResumeOnRestart,
        ResumePrompt = overlay.ResumePrompt ?? baseConfig.ResumePrompt,
        Environment = MergeDictionaries(baseConfig.Environment, overlay.Environment),
        ClaudeArgs = ConcatArrays(baseConfig.ClaudeArgs, overlay.ClaudeArgs),
        NameTemplate = overlay.NameTemplate ?? baseConfig.NameTemplate,
        PromptTemplate = overlay.PromptTemplate ?? baseConfig.PromptTemplate,
        ScriptsCreateFolder = overlay.ScriptsCreateFolder ?? baseConfig.ScriptsCreateFolder,
        Model = overlay.Model ?? baseConfig.Model,
        AllowSkipPermissions = overlay.AllowSkipPermissions ?? baseConfig.AllowSkipPermissions,
        PermissionMode = overlay.PermissionMode ?? baseConfig.PermissionMode
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
            ScriptsCreateFolder: raw.ScriptsCreateFolder ?? false,
            Model: raw.Model,
            // One script: its output is the answer, and two would give two
            Status: NormalizeScriptPaths(raw.Status, godModeRootPath) is [var status] ? status : null,
            ResumeOnRestart: raw.ResumeOnRestart ?? true,
            ResumePrompt: raw.ResumePrompt is { Length: > 0 } resumePrompt ? resumePrompt : CreateAction.DefaultResumePrompt,
            AllowSkipPermissions: raw.AllowSkipPermissions ?? false,
            PermissionMode: raw.PermissionMode
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
            return ReadSettled(schemaPath, json => JsonSerializer.Deserialize<JsonElement>(json, JsonOptions));
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
        public JsonElement? Status { get; init; }
        public bool? ResumeOnRestart { get; init; }
        public string? ResumePrompt { get; init; }
        public Dictionary<string, string>? Environment { get; init; }
        public string[]? ClaudeArgs { get; init; }
        public string? NameTemplate { get; init; }
        public string? PromptTemplate { get; init; }
        public bool? ScriptsCreateFolder { get; init; }
        public bool? StripEnvVarProfile { get; init; }
        public string? Model { get; init; }
        public bool? AllowSkipPermissions { get; init; }
        public string? PermissionMode { get; init; }

        /// <summary>Keys this reader does not know, such as a leftover MCP server config.</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Unrecognized { get; init; }
    }
}
