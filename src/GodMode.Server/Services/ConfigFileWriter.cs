using System.Text.Json;
using System.Text.Json.Nodes;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Writes configuration changes directly to appsettings.json using JsonNode for in-place editing.
/// Single source of truth — no shadow config stores.
/// </summary>
public class ConfigFileWriter
{
    private readonly string _configPath;
    private readonly ILogger<ConfigFileWriter> _logger;
    private readonly object _writeLock = new();

    public ConfigFileWriter(IWebHostEnvironment env, ILogger<ConfigFileWriter> logger)
    {
        _configPath = Path.Combine(env.ContentRootPath, "appsettings.json");
        _logger = logger;
    }

    public void CreateProfile(string name, string? description)
    {
        lock (_writeLock)
        {
            var root = ReadConfig();
            var profiles = EnsureProfilesSection(root);

            if (profiles[name] != null)
                throw new InvalidOperationException($"Profile '{name}' already exists.");

            var profile = new JsonObject();
            if (description != null)
                profile["Description"] = description;
            profile["Roots"] = new JsonObject();

            profiles[name] = profile;
            WriteConfig(root);
        }

        _logger.LogInformation("Created profile '{ProfileName}' in appsettings.json", name);
    }

    public void DeleteProfile(string name)
    {
        lock (_writeLock)
        {
            var root = ReadConfig();
            var profiles = EnsureProfilesSection(root);

            // Remove from appsettings if it exists there (may be auto-discovered only)
            profiles.Remove(name);
            WriteConfig(root);
        }

        _logger.LogInformation("Deleted profile '{ProfileName}' from appsettings.json", name);
    }

    public void UpdateProfileDescription(string name, string? description)
    {
        lock (_writeLock)
        {
            var root = ReadConfig();
            var profiles = EnsureProfilesSection(root);
            var profile = profiles[name]?.AsObject()
                ?? throw new KeyNotFoundException($"Profile '{name}' not found.");

            if (description != null)
                profile["Description"] = description;
            else
                profile.Remove("Description");

            WriteConfig(root);
        }

        _logger.LogInformation("Updated description for profile '{ProfileName}'", name);
    }

    public void AddMcpServerToProfile(string profileName, string serverName, McpServerConfig config)
    {
        lock (_writeLock)
        {
            var root = ReadConfig();
            var profiles = EnsureProfilesSection(root);
            var profile = profiles[profileName]?.AsObject();
            if (profile == null)
            {
                // Auto-create profile section for auto-discovered profiles
                profile = new JsonObject { ["Roots"] = new JsonObject() };
                profiles[profileName] = profile;
            }

            var mcpServers = profile["McpServers"]?.AsObject() ?? new JsonObject();
            profile["McpServers"] = mcpServers;

            var serverNode = new JsonObject { ["Command"] = config.Command };
            if (config.Args is { Length: > 0 })
            {
                var argsArray = new JsonArray();
                foreach (var arg in config.Args) argsArray.Add(arg);
                serverNode["Args"] = argsArray;
            }
            if (config.Env is { Count: > 0 })
            {
                var envObj = new JsonObject();
                foreach (var (k, v) in config.Env) envObj[k] = v;
                serverNode["Env"] = envObj;
            }

            mcpServers[serverName] = serverNode;
            WriteConfig(root);
        }

        _logger.LogInformation("Added MCP server '{ServerName}' to profile '{ProfileName}'", serverName, profileName);
    }

    public void RemoveMcpServerFromProfile(string profileName, string serverName)
    {
        lock (_writeLock)
        {
            var root = ReadConfig();
            var profiles = EnsureProfilesSection(root);
            var profile = profiles[profileName]?.AsObject()
                ?? throw new KeyNotFoundException($"Profile '{profileName}' not found.");

            var mcpServers = profile["McpServers"]?.AsObject();
            if (mcpServers == null || mcpServers[serverName] == null)
                throw new KeyNotFoundException($"MCP server '{serverName}' not found in profile '{profileName}'.");

            mcpServers.Remove(serverName);

            // Clean up empty McpServers section
            if (mcpServers.Count == 0)
                profile.Remove("McpServers");

            WriteConfig(root);
        }

        _logger.LogInformation("Removed MCP server '{ServerName}' from profile '{ProfileName}'", serverName, profileName);
    }

    private JsonObject ReadConfig()
    {
        if (!File.Exists(_configPath))
            return new JsonObject();

        var json = File.ReadAllText(_configPath);
        return JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
    }

    private void WriteConfig(JsonObject root)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = root.ToJsonString(options);
        File.WriteAllText(_configPath, json);
    }

    private static JsonObject EnsureProfilesSection(JsonObject root)
    {
        var profiles = root["Profiles"]?.AsObject();
        if (profiles == null)
        {
            profiles = new JsonObject();
            root["Profiles"] = profiles;
        }
        return profiles;
    }
}
