# Task: Implement MCP Server Discovery, Configuration, and Runtime Injection

You are implementing MCP (Model Context Protocol) server support for GodMode. This includes:
1. A discovery service to search/browse MCP servers from the Smithery registry
2. Configuration storage at profile, root, and action levels with proper merging
3. Runtime injection that passes merged MCP config to Claude Code processes
4. SignalR hub methods for client-server communication
5. Avalonia UI for browsing, selecting, and configuring MCP servers

Read this entire document before starting. Follow the GodMode patterns described in `CLAUDE.md`.

---

## Architecture Overview

GodMode has a hierarchical config system for Claude Code instances:

```
Server (appsettings.json)
  └─ Profile (ProfileConfig)        → shared env, now also shared mcpServers
      └─ Root (.godmode-root/config.json)  → base config, now also mcpServers
          └─ Action (config.{action}.json) → overlay, now also mcpServers
              └─ Project (.godmode/)       → runtime: merged mcp-config.json written here
```

MCP servers are configured as `Dictionary<string, McpServerConfig?>` at each level. They merge by server name (overlay wins). A `null` value explicitly removes an inherited server.

---

## Step 1: New Models in `GodMode.Shared`

### 1a. `src/GodMode.Shared/Models/McpServerConfig.cs` (NEW)

```csharp
using System.Text.Json.Serialization;

namespace GodMode.Shared.Models;

/// <summary>
/// Configuration for a single MCP server instance.
/// Matches Claude Code's native mcpServers format for direct compatibility.
/// Supports stdio (command+args) and HTTP/streamable (url) connection types.
/// </summary>
public record McpServerConfig(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Command = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string[]? Args = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Dictionary<string, string>? Env = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Url = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Type = null
);
```

### 1b. `src/GodMode.Shared/Models/McpRegistry.cs` (NEW)

Models for the Smithery registry API:

```csharp
using System.Text.Json.Serialization;

namespace GodMode.Shared.Models;

/// <summary>
/// A server entry from the Smithery MCP registry search results.
/// </summary>
public record McpRegistryServer(
    string QualifiedName,
    string DisplayName,
    string? Description = null,
    string? IconUrl = null,
    bool Verified = false,
    int UseCount = 0,
    bool Remote = false,
    bool IsDeployed = false,
    string? Homepage = null
);

/// <summary>
/// Paginated search results from the Smithery registry.
/// </summary>
public record McpRegistrySearchResult(
    McpRegistryServer[] Servers,
    McpRegistryPagination Pagination
);

public record McpRegistryPagination(
    int CurrentPage,
    int PageSize,
    int TotalPages,
    int TotalCount
);

/// <summary>
/// Full detail for a single MCP server from the Smithery registry.
/// Includes connection config, available tools, and auth requirements.
/// </summary>
public record McpServerDetail(
    string QualifiedName,
    string DisplayName,
    string? Description = null,
    string? IconUrl = null,
    bool Remote = false,
    string? DeploymentUrl = null,
    McpServerConnection[]? Connections = null,
    McpServerTool[]? Tools = null
);

/// <summary>
/// A connection method for an MCP server (stdio or http).
/// configSchema is a JSON Schema describing required config fields (API keys, tokens, etc).
/// </summary>
public record McpServerConnection(
    string Type,
    string? DeploymentUrl = null,

    [property: JsonPropertyName("configSchema")]
    System.Text.Json.JsonElement? ConfigSchema = null
);

public record McpServerTool(
    string Name,
    string? Description = null
);
```

---

## Step 2: MCP Registry Client

### `src/GodMode.Shared/Services/McpRegistryClient.cs` (NEW)

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Shared.Services;

/// <summary>
/// Client for the Smithery MCP server registry API.
/// Used by both server (via SignalR hub) and potentially client-side.
/// </summary>
public class McpRegistryClient
{
    private readonly HttpClient _http;
    private const string BaseUrl = "https://registry.smithery.ai";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public McpRegistryClient(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// Search for MCP servers by query string.
    /// </summary>
    public async Task<McpRegistrySearchResult> SearchAsync(
        string query, int pageSize = 20, int page = 1, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/servers?q={Uri.EscapeDataString(query)}&pageSize={pageSize}&page={page}";
        var result = await _http.GetFromJsonAsync<McpRegistrySearchResult>(url, JsonOptions, ct);
        return result ?? new McpRegistrySearchResult([], new McpRegistryPagination(1, pageSize, 0, 0));
    }

    /// <summary>
    /// Get full detail for a specific MCP server by qualified name.
    /// </summary>
    public async Task<McpServerDetail?> GetServerDetailAsync(
        string qualifiedName, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/servers/{Uri.EscapeDataString(qualifiedName)}";
        return await _http.GetFromJsonAsync<McpServerDetail>(url, JsonOptions, ct);
    }
}
```

Register in DI in `GodMode.Server/Program.cs`:
```csharp
builder.Services.AddHttpClient<McpRegistryClient>();
```

---

## Step 3: Add `McpServers` to Config Models

### 3a. `src/GodMode.Shared/Models/CreateAction.cs`

Add `McpServers` parameter to the existing record:

```csharp
public record CreateAction(
    string Name,
    string? Description = null,
    JsonElement? InputSchema = null,
    string[]? Prepare = null,
    string[]? Create = null,
    string[]? Delete = null,
    Dictionary<string, string>? Environment = null,
    string[]? ClaudeArgs = null,
    string? NameTemplate = null,
    string? PromptTemplate = null,
    bool ScriptsCreateFolder = false,
    string? Model = null,
    Dictionary<string, McpServerConfig?>? McpServers = null  // <-- ADD THIS
);
```

### 3b. `src/GodMode.Server/Models/ProfileConfig.cs`

Add `McpServers` property:

```csharp
public class ProfileConfig
{
    public Dictionary<string, string> Roots { get; set; } = new();
    public Dictionary<string, string>? Environment { get; set; }
    public string? Description { get; set; }
    public Dictionary<string, McpServerConfig?>? McpServers { get; set; }  // <-- ADD THIS
}
```

Add the import: `using GodMode.Shared.Models;`

### 3c. `src/GodMode.Server/Services/RootConfigReader.cs`

**Add to `RawConfig` record** (bottom of file, around line 258):
```csharp
public Dictionary<string, McpServerConfig?>? McpServers { get; init; }
```

**Add to `MergeRawConfigs`** (around line 146):
```csharp
McpServers = MergeMcpServers(baseConfig.McpServers, overlay.McpServers),
```

**Add to `BuildAction`** (around line 164):
Pass `McpServers: raw.McpServers` in the CreateAction constructor.

**Add new merge method** (after `MergeDictionaries`):
```csharp
/// <summary>
/// Merges MCP server dictionaries. Overlay servers override base by name.
/// Null values in overlay explicitly remove inherited servers.
/// </summary>
private static Dictionary<string, McpServerConfig?>? MergeMcpServers(
    Dictionary<string, McpServerConfig?>? baseDict,
    Dictionary<string, McpServerConfig?>? overrideDict)
{
    if (baseDict == null) return overrideDict;
    if (overrideDict == null) return baseDict;

    var merged = new Dictionary<string, McpServerConfig?>(baseDict, StringComparer.OrdinalIgnoreCase);
    foreach (var (key, value) in overrideDict)
        merged[key] = value;
    return merged;
}
```

Add import: `using GodMode.Shared.Models;`

---

## Step 4: Runtime Injection in ProjectManager

### 4a. Modify `BuildClaudeConfig` in `src/GodMode.Server/Services/ProjectManager.cs`

Change the signature to accept MCP servers from both profile and action:

```csharp
private static (Dictionary<string, string>? Env, string[]? Args) BuildClaudeConfig(
    CreateAction action, ProjectFiles.ProjectSettings settings,
    string? model = null,
    Dictionary<string, string>? profileEnv = null,
    string? profileName = null,
    bool stripEnvVarProfile = false,
    Dictionary<string, McpServerConfig?>? profileMcpServers = null,  // <-- ADD
    string? projectPath = null)                                       // <-- ADD
```

**Add MCP config writing logic** at the end, before the return:

```csharp
// Merge MCP servers: profile → action (action wins per key, null = remove)
var mcpServers = MergeMcpServers(profileMcpServers, action.McpServers);
if (mcpServers is { Count: > 0 } && projectPath != null)
{
    // Expand ${VAR} in MCP server env values
    foreach (var (name, config) in mcpServers)
    {
        if (config?.Env != null)
        {
            var expandedEnv = EnvironmentExpander.ExpandVariables(config.Env);
            if (expandedEnv != config.Env)
            {
                mcpServers[name] = config with { Env = expandedEnv };
            }
        }
    }

    // Write .godmode/mcp-config.json
    var mcpConfigPath = Path.Combine(projectPath, ".godmode", "mcp-config.json");
    var mcpPayload = new Dictionary<string, object> { ["mcpServers"] = mcpServers };
    var json = JsonSerializer.Serialize(mcpPayload, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    });
    File.WriteAllText(mcpConfigPath, json);

    args.Add("--mcp-config");
    args.Add(Path.Combine(".godmode", "mcp-config.json"));
}
```

**Add a local `MergeMcpServers` method** in ProjectManager (or reuse via a shared static helper):

```csharp
/// <summary>
/// Merges MCP server configs from two levels. Overlay wins per key. Null entries are filtered out.
/// </summary>
private static Dictionary<string, McpServerConfig?>? MergeMcpServers(
    Dictionary<string, McpServerConfig?>? baseDict,
    Dictionary<string, McpServerConfig?>? overrideDict)
{
    if (baseDict == null && overrideDict == null) return null;

    var merged = new Dictionary<string, McpServerConfig?>(StringComparer.OrdinalIgnoreCase);
    if (baseDict != null)
        foreach (var (k, v) in baseDict) merged[k] = v;
    if (overrideDict != null)
        foreach (var (k, v) in overrideDict) merged[k] = v;

    // Remove null entries (explicit removals)
    var toRemove = merged.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList();
    foreach (var key in toRemove) merged.Remove(key);

    return merged.Count > 0 ? merged : null;
}
```

### 4b. Update all call sites of `BuildClaudeConfig`

There are two call sites in ProjectManager:

**In `CreateProjectAsync`** (around line 511-516):
Pass the profile's McpServers and the projectPath:
```csharp
var profileMcpServers = profileCfg?.McpServers;
(claudeEnv, claudeArgs) = BuildClaudeConfig(action, settings, model, profileEnv,
    profileName, stripEnvVarProfile, profileMcpServers, project.ProjectPath);
```

**In `ResumeProjectAsync`** (around line 697-708):
Pass the profile's McpServers and the projectPath:
```csharp
var profileMcpServers = profileCfg?.McpServers;
(claudeEnv, claudeArgs) = BuildClaudeConfig(action, settings, action.Model, profileEnv,
    resumeProfileName, config.StripEnvVarProfile, profileMcpServers, project.ProjectPath);
```

Also update the fallback paths (no action found) to pass `profileMcpServers` and `project.ProjectPath`.

---

## Step 5: Profile Runtime Persistence

### `src/GodMode.Server/Services/ProfileOverrideStore.cs` (NEW)

Profile-level MCP servers (added via UI) are persisted in `~/.godmode/profiles.json`, separate from the static `appsettings.json`.

```csharp
using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Persists runtime profile overrides (McpServers, Environment additions) to ~/.godmode/profiles.json.
/// These are merged on top of appsettings.json ProfileConfig at runtime.
/// </summary>
public class ProfileOverrideStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".godmode", "profiles.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Reads all profile overrides from disk.
    /// </summary>
    public Dictionary<string, ProfileOverride> Load()
    {
        if (!File.Exists(StorePath))
            return new Dictionary<string, ProfileOverride>(StringComparer.OrdinalIgnoreCase);

        var json = File.ReadAllText(StorePath);
        return JsonSerializer.Deserialize<Dictionary<string, ProfileOverride>>(json, JsonOptions)
            ?? new Dictionary<string, ProfileOverride>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Saves all profile overrides to disk.
    /// </summary>
    public void Save(Dictionary<string, ProfileOverride> overrides)
    {
        var dir = Path.GetDirectoryName(StorePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(overrides, JsonOptions));
    }

    /// <summary>
    /// Adds or updates an MCP server in a profile's overrides.
    /// </summary>
    public void SetMcpServer(string profileName, string serverName, McpServerConfig? config)
    {
        var overrides = Load();
        if (!overrides.TryGetValue(profileName, out var profile))
        {
            profile = new ProfileOverride();
            overrides[profileName] = profile;
        }
        profile.McpServers ??= new Dictionary<string, McpServerConfig?>(StringComparer.OrdinalIgnoreCase);
        profile.McpServers[serverName] = config;
        Save(overrides);
    }

    /// <summary>
    /// Removes an MCP server from a profile's overrides.
    /// </summary>
    public void RemoveMcpServer(string profileName, string serverName)
    {
        var overrides = Load();
        if (overrides.TryGetValue(profileName, out var profile) && profile.McpServers != null)
        {
            profile.McpServers.Remove(serverName);
            if (profile.McpServers.Count == 0) profile.McpServers = null;
            if (profile.IsEmpty) overrides.Remove(profileName);
            Save(overrides);
        }
    }
}

public class ProfileOverride
{
    public Dictionary<string, McpServerConfig?>? McpServers { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => McpServers is null or { Count: 0 };
}
```

Register in DI: `builder.Services.AddSingleton<ProfileOverrideStore>();`

### Merge overrides into ProfileConfig

In ProjectManager, wherever profiles are loaded from `_snapshot.Profiles`, merge the override store's MCP servers on top. The cleanest approach: when building the snapshot, apply overrides:

In the snapshot-building code, after loading profiles from config:
```csharp
var overrides = _profileOverrideStore.Load();
foreach (var (name, profileOverride) in overrides)
{
    if (profiles.TryGetValue(name, out var profile) && profileOverride.McpServers != null)
    {
        profile.McpServers = MergeMcpServers(profile.McpServers, profileOverride.McpServers)
            as Dictionary<string, McpServerConfig?>;
    }
}
```

---

## Step 6: Root/Action Config File Writing

When `AddMcpServer` targets a root or action level, the server must read-modify-write the config JSON file.

### Add to ProjectManager or a new `McpConfigWriter` helper:

```csharp
/// <summary>
/// Adds or removes an MCP server in a .godmode-root config file.
/// </summary>
private void WriteMcpServerToConfigFile(string configFilePath, string serverName, McpServerConfig? config)
{
    var json = File.Exists(configFilePath) ? File.ReadAllText(configFilePath) : "{}";
    var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOptions)
        ?? new Dictionary<string, JsonElement>();

    // Parse or create mcpServers section
    var mcpServers = doc.ContainsKey("mcpServers")
        ? JsonSerializer.Deserialize<Dictionary<string, McpServerConfig?>>(doc["mcpServers"].GetRawText(), JsonOptions)
            ?? new Dictionary<string, McpServerConfig?>()
        : new Dictionary<string, McpServerConfig?>();

    if (config != null)
        mcpServers[serverName] = config;
    else
        mcpServers.Remove(serverName);

    doc["mcpServers"] = JsonSerializer.SerializeToElement(
        mcpServers.Count > 0 ? mcpServers : null, JsonOptions);

    if (doc["mcpServers"].ValueKind == JsonValueKind.Null)
        doc.Remove("mcpServers");

    File.WriteAllText(configFilePath, JsonSerializer.Serialize(doc, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    }));
}
```

---

## Step 7: SignalR Hub Methods

### 7a. Add to `IProjectHub` (`src/GodMode.Shared/Hubs/IProjectHub.cs`)

```csharp
// MCP Server Discovery & Configuration
Task<McpRegistrySearchResult> SearchMcpServers(string query, int pageSize = 20, int page = 1);
Task<McpServerDetail?> GetMcpServerDetail(string qualifiedName);
Task AddMcpServer(string serverName, McpServerConfig config, string targetLevel,
    string? profileName = null, string? rootName = null, string? actionName = null);
Task RemoveMcpServer(string serverName, string targetLevel,
    string? profileName = null, string? rootName = null, string? actionName = null);
Task<Dictionary<string, McpServerConfig>> GetEffectiveMcpServers(
    string profileName, string rootName, string? actionName = null);
```

### 7b. Implement in `ProjectHub` (`src/GodMode.Server/Hubs/ProjectHub.cs`)

Follow the existing delegation pattern — each method logs and delegates to `IProjectManager`:

```csharp
public async Task<McpRegistrySearchResult> SearchMcpServers(string query, int pageSize, int page)
{
    _logger.LogInformation("Client {ConnectionId} searching MCP servers: '{Query}'",
        Context.ConnectionId, query);
    return await _projectManager.SearchMcpServersAsync(query, pageSize, page);
}

public async Task<McpServerDetail?> GetMcpServerDetail(string qualifiedName)
{
    _logger.LogInformation("Client {ConnectionId} getting MCP server detail: '{Name}'",
        Context.ConnectionId, qualifiedName);
    return await _projectManager.GetMcpServerDetailAsync(qualifiedName);
}

public async Task AddMcpServer(string serverName, McpServerConfig config, string targetLevel,
    string? profileName, string? rootName, string? actionName)
{
    _logger.LogInformation("Client {ConnectionId} adding MCP server '{Server}' to {Level}",
        Context.ConnectionId, serverName, targetLevel);
    await _projectManager.AddMcpServerAsync(serverName, config, targetLevel,
        profileName, rootName, actionName);
}

public async Task RemoveMcpServer(string serverName, string targetLevel,
    string? profileName, string? rootName, string? actionName)
{
    _logger.LogInformation("Client {ConnectionId} removing MCP server '{Server}' from {Level}",
        Context.ConnectionId, serverName, targetLevel);
    await _projectManager.RemoveMcpServerAsync(serverName, targetLevel,
        profileName, rootName, actionName);
}

public async Task<Dictionary<string, McpServerConfig>> GetEffectiveMcpServers(
    string profileName, string rootName, string? actionName)
{
    _logger.LogInformation("Client {ConnectionId} getting effective MCP servers for {Profile}/{Root}/{Action}",
        Context.ConnectionId, profileName, rootName, actionName ?? "(all)");
    return await _projectManager.GetEffectiveMcpServersAsync(profileName, rootName, actionName);
}
```

### 7c. Add to `IProjectManager` (`src/GodMode.Server/Services/IProjectManager.cs`)

```csharp
Task<McpRegistrySearchResult> SearchMcpServersAsync(string query, int pageSize = 20, int page = 1);
Task<McpServerDetail?> GetMcpServerDetailAsync(string qualifiedName);
Task AddMcpServerAsync(string serverName, McpServerConfig config, string targetLevel,
    string? profileName, string? rootName, string? actionName);
Task RemoveMcpServerAsync(string serverName, string targetLevel,
    string? profileName, string? rootName, string? actionName);
Task<Dictionary<string, McpServerConfig>> GetEffectiveMcpServersAsync(
    string profileName, string rootName, string? actionName);
```

### 7d. Implement in `ProjectManager`

```csharp
public Task<McpRegistrySearchResult> SearchMcpServersAsync(string query, int pageSize, int page)
    => _mcpRegistryClient.SearchAsync(query, pageSize, page);

public Task<McpServerDetail?> GetMcpServerDetailAsync(string qualifiedName)
    => _mcpRegistryClient.GetServerDetailAsync(qualifiedName);

public Task AddMcpServerAsync(string serverName, McpServerConfig config, string targetLevel,
    string? profileName, string? rootName, string? actionName)
{
    switch (targetLevel.ToLowerInvariant())
    {
        case "profile":
            ArgumentNullException.ThrowIfNull(profileName);
            _profileOverrideStore.SetMcpServer(profileName, serverName, config);
            break;

        case "root":
            ArgumentNullException.ThrowIfNull(profileName);
            ArgumentNullException.ThrowIfNull(rootName);
            var rootPath = _snapshot.ProjectFiles.GetProjectRootPath(CompositeKey(profileName, rootName));
            var configPath = Path.Combine(rootPath, ".godmode-root", "config.json");
            WriteMcpServerToConfigFile(configPath, serverName, config);
            break;

        case "action":
            ArgumentNullException.ThrowIfNull(profileName);
            ArgumentNullException.ThrowIfNull(rootName);
            ArgumentNullException.ThrowIfNull(actionName);
            var actionRootPath = _snapshot.ProjectFiles.GetProjectRootPath(CompositeKey(profileName, rootName));
            var actionConfigPath = Path.Combine(actionRootPath, ".godmode-root", $"config.{actionName}.json");
            WriteMcpServerToConfigFile(actionConfigPath, serverName, config);
            break;

        default:
            throw new ArgumentException($"Unknown target level: {targetLevel}");
    }
    return Task.CompletedTask;
}

public Task RemoveMcpServerAsync(string serverName, string targetLevel,
    string? profileName, string? rootName, string? actionName)
{
    switch (targetLevel.ToLowerInvariant())
    {
        case "profile":
            ArgumentNullException.ThrowIfNull(profileName);
            _profileOverrideStore.RemoveMcpServer(profileName, serverName);
            break;

        case "root":
            ArgumentNullException.ThrowIfNull(profileName);
            ArgumentNullException.ThrowIfNull(rootName);
            var rootPath = _snapshot.ProjectFiles.GetProjectRootPath(CompositeKey(profileName, rootName));
            WriteMcpServerToConfigFile(
                Path.Combine(rootPath, ".godmode-root", "config.json"), serverName, null);
            break;

        case "action":
            ArgumentNullException.ThrowIfNull(profileName);
            ArgumentNullException.ThrowIfNull(rootName);
            ArgumentNullException.ThrowIfNull(actionName);
            var actionRootPath = _snapshot.ProjectFiles.GetProjectRootPath(CompositeKey(profileName, rootName));
            WriteMcpServerToConfigFile(
                Path.Combine(actionRootPath, ".godmode-root", $"config.{actionName}.json"), serverName, null);
            break;
    }
    return Task.CompletedTask;
}

public async Task<Dictionary<string, McpServerConfig>> GetEffectiveMcpServersAsync(
    string profileName, string rootName, string? actionName)
{
    // Profile level
    _snapshot.Profiles.TryGetValue(profileName, out var profileCfg);
    var profileMcp = profileCfg?.McpServers;

    // Root + action level
    var rootPath = _snapshot.ProjectFiles.GetProjectRootPath(CompositeKey(profileName, rootName));
    var config = _rootConfigReader.ReadConfig(rootPath);
    var action = config.ResolveAction(actionName);
    var actionMcp = action?.McpServers;

    // Merge all levels
    var merged = MergeMcpServers(profileMcp, actionMcp);

    // Filter nulls and return non-nullable dict
    return merged?
        .Where(kv => kv.Value != null)
        .ToDictionary(kv => kv.Key, kv => kv.Value!)
        ?? new Dictionary<string, McpServerConfig>();
}
```

Inject `McpRegistryClient` and `ProfileOverrideStore` via constructor.

---

## Step 8: Client-Side Service

### Add to `IProjectService` (`src/GodMode.ClientBase/Services/IProjectService.cs`)

```csharp
Task<McpRegistrySearchResult> SearchMcpServersAsync(string profileName, string hostId, string query, int pageSize = 20, int page = 1);
Task<McpServerDetail?> GetMcpServerDetailAsync(string profileName, string hostId, string qualifiedName);
Task AddMcpServerAsync(string profileName, string hostId, string serverName, McpServerConfig config,
    string targetLevel, string? serverProfileName, string? rootName, string? actionName);
Task RemoveMcpServerAsync(string profileName, string hostId, string serverName,
    string targetLevel, string? serverProfileName, string? rootName, string? actionName);
Task<Dictionary<string, McpServerConfig>> GetEffectiveMcpServersAsync(
    string profileName, string hostId, string serverProfileName, string rootName, string? actionName);
```

### Implement in `SignalRProjectConnection`

Follow the existing pattern — each method calls the hub connection method. Find `SignalRProjectConnection` in `src/GodMode.ClientBase/` and add implementations that invoke the hub.

---

## Step 9: Avalonia UI

### 9a. MCP Browser View

**File:** `src/GodMode.Avalonia/Views/McpBrowserView.axaml` (NEW)

Layout:
- Search box with debounced input (300ms)
- Results list: icon (from IconUrl), display name, description (truncated), verified badge, use count
- Detail panel (shown when a server is selected): full description, tools list, connection type
- Config form: dynamic fields from `configSchema` (reuse `FormFieldParser` and `FormFieldTemplateSelector`)
- Target level selector: ComboBox with "Profile", "Root", "Action" options
- "Add Server" button

### 9b. MCP Browser ViewModel

**File:** `src/GodMode.Avalonia/ViewModels/McpBrowserViewModel.cs` (NEW)

Follow `CreateProjectViewModel` patterns:

```csharp
public partial class McpBrowserViewModel : ViewModelBase
{
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _isLoadingDetail;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private McpServerDetail? _selectedServerDetail;

    public ObservableCollection<McpRegistryServer> SearchResults { get; } = new();
    public ObservableCollection<FormField> ConfigFields { get; } = new();

    // Target level for adding
    [ObservableProperty] private string _selectedTargetLevel = "Profile";
    public string[] TargetLevels { get; } = ["Profile", "Root", "Action"];

    // Context: which profile/root/action are we configuring for
    [ObservableProperty] private string? _profileName;
    [ObservableProperty] private string? _rootName;
    [ObservableProperty] private string? _actionName;

    [RelayCommand]
    private async Task SearchAsync() { /* call service, populate SearchResults */ }

    [RelayCommand]
    private async Task SelectServerAsync(McpRegistryServer server)
    {
        // Fetch detail, parse configSchema into ConfigFields using FormFieldParser
        var detail = await _projectService.GetMcpServerDetailAsync(..., server.QualifiedName);
        SelectedServerDetail = detail;

        ConfigFields.Clear();
        if (detail?.Connections?.FirstOrDefault()?.ConfigSchema is { } schema)
        {
            foreach (var field in FormFieldParser.Parse(schema))
                ConfigFields.Add(field);
        }
    }

    [RelayCommand]
    private async Task AddServerAsync()
    {
        // Build McpServerConfig from detail + filled config fields
        var connection = SelectedServerDetail?.Connections?.FirstOrDefault();
        var env = ConfigFields
            .Where(f => !string.IsNullOrEmpty(f.Value))
            .ToDictionary(f => f.Key, f => f.Value);

        var config = connection?.Type == "http"
            ? new McpServerConfig(Url: connection.DeploymentUrl, Env: env.Count > 0 ? env : null)
            : new McpServerConfig(Command: "npx", Args: new[] { "-y", $"@smithery/cli@latest", "run", SelectedServerDetail!.QualifiedName }, Env: env.Count > 0 ? env : null);

        await _projectService.AddMcpServerAsync(..., SelectedServerDetail.QualifiedName,
            config, SelectedTargetLevel.ToLower(), ProfileName, RootName, ActionName);
    }
}
```

### 9c. MCP Servers Panel in CreateProjectView

Add a collapsible section in `CreateProjectView.axaml` (after the model selector area):

- Heading: "MCP Servers" with count badge
- List of effective servers with: name, type badge (remote/local), source badge (profile/root/action)
- "Browse & Add" button that opens the McpBrowserView (as dialog or flyout)
- Remove button per server

Load effective servers in `CreateProjectViewModel` when profile/root/action selection changes:
```csharp
partial void OnSelectedActionChanged(CreateActionInfo? value)
{
    // ... existing logic ...
    _ = LoadEffectiveMcpServersAsync();
}

private async Task LoadEffectiveMcpServersAsync()
{
    if (SelectedProfile == null || SelectedProjectRoot == null) return;
    var servers = await _projectService.GetEffectiveMcpServersAsync(...,
        SelectedProfile.Name, SelectedProjectRoot.Name, SelectedAction?.Name);
    EffectiveMcpServers.Clear();
    foreach (var (name, config) in servers)
        EffectiveMcpServers.Add(new McpServerDisplay(name, config));
}
```

---

## Step 10: Config Example

After implementation, a complete setup looks like:

**Profile** (`~/.godmode/profiles.json`):
```json
{
  "GodMode Dev": {
    "mcpServers": {
      "github": {
        "url": "https://github.run.tools",
        "env": { "GITHUB_TOKEN": "ghp_xxxx" }
      }
    }
  }
}
```

**Root** (`.godmode-root/config.json`):
```json
{
  "description": "GodMode development worktrees",
  "mcpServers": {
    "filesystem": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "/data"]
    }
  }
}
```

**Action** (`.godmode-root/config.issue.json`):
```json
{
  "description": "Create project from GitHub issue",
  "mcpServers": {
    "linear": { "command": "npx", "args": ["-y", "@linear/mcp-server"] },
    "filesystem": null
  }
}
```

**Result** (`.godmode/mcp-config.json` — auto-generated):
```json
{
  "mcpServers": {
    "github": {
      "url": "https://github.run.tools",
      "env": { "GITHUB_TOKEN": "ghp_xxxx" }
    },
    "linear": {
      "command": "npx",
      "args": ["-y", "@linear/mcp-server"]
    }
  }
}
```
Note: `filesystem` is absent because the action removed it with `null`.

Claude process receives: `--mcp-config .godmode/mcp-config.json`

---

## Verification

### Build
```bash
dotnet build
```
Must compile cleanly with no warnings related to new code.

### Unit Tests

Create `tests/GodMode.Server.Tests/McpMergeTests.cs`:
- Test MergeMcpServers with base only, overlay only, both, null removal
- Test ${VAR} expansion in MCP server env values
- Test config file read-modify-write cycle

Create `tests/GodMode.Server.Tests/McpRegistryClientTests.cs`:
- Mock HttpClient, test search parsing, detail parsing

### Integration Test
1. Create temp root directory with `.godmode-root/config.json` containing `mcpServers`
2. Create `config.test.json` action with mcpServers including a null override
3. Call the merge pipeline
4. Verify `.godmode/mcp-config.json` contains correct merged result
5. Verify `--mcp-config` is in the returned args

### Manual E2E
1. Start server: `dotnet run --project src/GodMode.Server/GodMode.Server.csproj`
2. Start desktop app: `dotnet run --project src/GodMode.Avalonia.Desktop/GodMode.Avalonia.Desktop.csproj`
3. Navigate to MCP browser, search for "github"
4. Select the GitHub server, fill in token, add to profile
5. Create a project — verify Claude reports the MCP server is available
6. Check `.godmode/mcp-config.json` in the project directory
7. Test resume — verify MCP config is rewritten fresh

---

## File Manifest

### New Files
| File | Description |
|------|-------------|
| `src/GodMode.Shared/Models/McpServerConfig.cs` | MCP server config record |
| `src/GodMode.Shared/Models/McpRegistry.cs` | Registry API response models |
| `src/GodMode.Shared/Services/McpRegistryClient.cs` | Smithery API client |
| `src/GodMode.Server/Services/ProfileOverrideStore.cs` | Runtime profile persistence |
| `src/GodMode.Avalonia/Views/McpBrowserView.axaml` | MCP browser UI |
| `src/GodMode.Avalonia/ViewModels/McpBrowserViewModel.cs` | MCP browser logic |
| `tests/GodMode.Server.Tests/McpMergeTests.cs` | Merge logic tests |
| `tests/GodMode.Server.Tests/McpRegistryClientTests.cs` | Registry client tests |

### Modified Files
| File | Change |
|------|--------|
| `src/GodMode.Shared/Models/CreateAction.cs` | Add `McpServers` parameter |
| `src/GodMode.Shared/Hubs/IProjectHub.cs` | Add 5 MCP hub methods |
| `src/GodMode.Server/Models/ProfileConfig.cs` | Add `McpServers` property |
| `src/GodMode.Server/Services/RootConfigReader.cs` | Add McpServers to RawConfig, merge method, BuildAction pass-through |
| `src/GodMode.Server/Services/ProjectManager.cs` | BuildClaudeConfig MCP injection, hub method implementations, config file writer |
| `src/GodMode.Server/Services/IProjectManager.cs` | Add 5 MCP method signatures |
| `src/GodMode.Server/Hubs/ProjectHub.cs` | Wire 5 MCP methods |
| `src/GodMode.Server/Program.cs` | Register McpRegistryClient and ProfileOverrideStore in DI |
| `src/GodMode.ClientBase/Services/IProjectService.cs` | Add MCP service methods |
| `src/GodMode.ClientBase/Services/SignalRProjectConnection.cs` | Implement MCP methods |
| `src/GodMode.Avalonia/Views/CreateProjectView.axaml` | Add MCP servers panel |
| `src/GodMode.Avalonia/ViewModels/CreateProjectViewModel.cs` | Load/display effective MCP servers |
