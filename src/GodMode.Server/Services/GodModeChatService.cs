using System.Collections.Concurrent;
using System.Text.Json;
using GodMode.AI;
using GodMode.Server.Hubs;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;

namespace GodMode.Server.Services;

/// <summary>
/// GodMode meta-management chat: an agentic AI loop that can CRUD roots, profiles,
/// MCP servers, and projects through tool calls.
/// </summary>
public sealed class GodModeChatService
{
    private const int MaxToolLoopIterations = 15;

    private readonly InferenceRouter _inference;
    private readonly IProjectManager _projectManager;
    private readonly ProfileFileManager _profileFileManager;
    private readonly RootGenerationService _rootGenService;
    private readonly IHubContext<ProjectHub, IProjectHubClient> _hubContext;
    private readonly ILogger<GodModeChatService> _logger;

    private readonly ConcurrentDictionary<string, List<ChatMessage>> _sessions = new();

    private static readonly string SystemPrompt = """
        You are GodMode — the AI control plane for managing Claude Code development environments.

        ## What you manage

        **Roots** — Project templates that define how Claude Code projects are created.
        Each root lives in a directory with a `.godmode-root/` folder containing:
        - `config.json`: Base configuration (description, nameTemplate, promptTemplate, scripts, environment, claudeArgs)
        - `config.{action}.json`: Per-action overlays (merged with base config)
        - `schema.json` or `{action}/schema.json`: JSON Schema for the UI input form
        - `scripts/`: Shell scripts (.sh for Linux/macOS, .ps1 for Windows)
          - `prepare.sh/ps1`: Runs before project creation (install deps, etc.)
          - `create.sh/ps1`: Runs during project creation
          - `delete.sh/ps1`: Cleanup when project is deleted

        **Profiles** — Named groups that organize roots. Each profile can have:
        - A description
        - Environment variables (applied to all projects in that profile)
        - MCP servers (tool servers available to Claude in that profile)

        **MCP Servers** — Model Context Protocol servers that give Claude additional tools.
        Defined as: command + args + environment variables.
        Example: `npx -y @modelcontextprotocol/server-github` with `GITHUB_TOKEN` env var.

        **Projects** — Running Claude Code instances. Created from a root template.
        Each project has a state (Idle, Running, WaitingInput, Stopped, Error).

        ## Your role

        Help the user manage their GodMode environment through natural conversation.
        When they ask you to create, modify, or delete resources — use the tools provided.

        When creating roots, think carefully about:
        - What scripts are needed (prepare, create, delete)
        - What input fields the user form should have (schema.json)
        - What environment variables or claudeArgs are useful
        - Cross-platform compatibility (provide both .sh and .ps1 when appropriate)

        ## Config.json format reference
        ```json
        {
          "description": "Human-readable description",
          "nameTemplate": "{name}",
          "promptTemplate": "{prompt}",
          "prepare": "scripts/prepare.sh",
          "create": "scripts/create.sh",
          "delete": "scripts/delete.sh",
          "environment": { "KEY": "value" },
          "claudeArgs": ["--flag"],
          "scriptsCreateFolder": false,
          "model": null,
          "profileName": "Default"
        }
        ```

        ## Schema.json format reference (JSON Schema)
        ```json
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "title": "Project Name" },
            "prompt": { "type": "string", "title": "Task Description", "x-multiline": true },
            "skipPermissions": { "type": "boolean", "title": "Skip Permissions", "default": "true" }
          },
          "required": ["name", "prompt"]
        }
        ```

        ## Script environment variables
        Scripts receive: GODMODE_ROOT_PATH, GODMODE_PROJECT_PATH, GODMODE_PROJECT_ID,
        GODMODE_PROJECT_NAME, GODMODE_RESULT_FILE, plus GODMODE_INPUT_{FIELD} for each form field.

        Be concise and helpful. Use tools to take action — don't just describe what you would do.
        """;

    public GodModeChatService(
        InferenceRouter inference,
        IProjectManager projectManager,
        ProfileFileManager profileFileManager,
        RootGenerationService rootGenService,
        IHubContext<ProjectHub, IProjectHubClient> hubContext,
        ILogger<GodModeChatService> logger)
    {
        _inference = inference;
        _projectManager = projectManager;
        _profileFileManager = profileFileManager;
        _rootGenService = rootGenService;
        _hubContext = hubContext;
        _logger = logger;
    }

    public void RemoveSession(string connectionId) => _sessions.TryRemove(connectionId, out _);

    public async Task ProcessMessageAsync(
        string connectionId,
        string userMessage,
        Func<ChatResponseMessage, Task> sendToClient,
        CancellationToken ct = default)
    {
        if (!_inference.IsLoaded)
        {
            await sendToClient(new ChatResponseMessage(ChatResponseType.Error,
                "Inference not available. Configure an API key in ~/.godmode/inference.json"));
            return;
        }

        var messages = _sessions.GetOrAdd(connectionId, _ => new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt)
        });

        // Inject current state context on first user message
        if (messages.Count == 1)
        {
            var stateContext = await BuildStateContextAsync();
            messages.Add(new ChatMessage(ChatRole.System, stateContext));
        }

        messages.Add(new ChatMessage(ChatRole.User, userMessage));

        var tools = BuildTools();
        var options = new ChatOptions
        {
            Tools = tools,
            MaxOutputTokens = 4096,
            Temperature = 0.3f,
        };

        // Agentic loop: call LLM, execute tools, repeat until text response
        for (var i = 0; i < MaxToolLoopIterations; i++)
        {
            var response = await _inference.GenerateAsync(InferenceTier.Heavy, messages, options, ct);
            if (response == null)
            {
                await sendToClient(new ChatResponseMessage(ChatResponseType.Error, "No inference provider available."));
                return;
            }

            // Add assistant response to history
            messages.Add(response.Messages.Last());

            // Check for tool calls
            var toolCalls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            if (toolCalls.Count == 0)
            {
                // Pure text response — send it
                var text = response.Text ?? "";
                await sendToClient(new ChatResponseMessage(ChatResponseType.Text, text));
                return;
            }

            // Execute tool calls
            foreach (var call in toolCalls)
            {
                await sendToClient(new ChatResponseMessage(ChatResponseType.ToolCall,
                    $"Calling {call.Name}...", call.Name));

                string result;
                try
                {
                    var tool = tools.OfType<AIFunction>().FirstOrDefault(t => t.Name == call.Name);
                    if (tool == null)
                    {
                        result = $"Error: unknown tool '{call.Name}'";
                    }
                    else
                    {
                        var args = call.Arguments != null
                            ? new AIFunctionArguments(call.Arguments)
                            : new AIFunctionArguments();
                        var invokeResult = await tool.InvokeAsync(args, ct);
                        result = invokeResult?.ToString() ?? "OK";
                    }
                }
                catch (Exception ex)
                {
                    result = $"Error: {ex.Message}";
                    _logger.LogWarning(ex, "Tool {ToolName} failed", call.Name);
                }

                await sendToClient(new ChatResponseMessage(ChatResponseType.ToolResult, result, call.Name));

                // Add tool result to conversation
                messages.Add(new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(call.CallId, result)]));
            }

            // Loop back for the LLM to process tool results
        }

        await sendToClient(new ChatResponseMessage(ChatResponseType.Error,
            "Reached maximum tool call iterations. Please try a simpler request."));
    }

    private async Task<string> BuildStateContextAsync()
    {
        try
        {
            var profiles = await _projectManager.ListProfilesAsync();
            var roots = await _projectManager.ListProjectRootsAsync();
            var projects = await _projectManager.ListProjectsAsync();

            return $"""
                ## Current GodMode State

                **Profiles** ({profiles.Length}): {string.Join(", ", profiles.Select(p => p.Name + (p.Description != null ? $" ({p.Description})" : "")))}

                **Roots** ({roots.Length}):
                {string.Join("\n", roots.Select(r => $"- {r.Name} [{r.ProfileName}]: {r.Description ?? "no description"} ({r.Actions?.Length ?? 0} actions)"))}

                **Projects** ({projects.Length}):
                {string.Join("\n", projects.Select(p => $"- {p.Name} [{p.State}] (root: {p.RootName}, profile: {p.ProfileName})"))}
                """;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build state context");
            return "## Current state could not be loaded.";
        }
    }

    private IList<AITool> BuildTools()
    {
        return
        [
            // ── Profiles ──
            AIFunctionFactory.Create(async (string name, string? description) =>
            {
                await _projectManager.CreateProfileAsync(name, description);
                await _hubContext.Clients.All.ProfilesChanged();
                return $"Created profile '{name}'.";
            }, "create_profile", "Create a new profile with optional description."),

            AIFunctionFactory.Create(async (string name, bool deleteContents) =>
            {
                await _projectManager.DeleteProfileAsync(name, deleteContents);
                await _hubContext.Clients.All.ProfilesChanged();
                return $"Deleted profile '{name}' (deleteContents={deleteContents}).";
            }, "delete_profile", "Delete a profile. Set deleteContents=true to also delete its roots and projects."),

            AIFunctionFactory.Create(async () =>
            {
                var profiles = await _projectManager.ListProfilesAsync();
                return JsonSerializer.Serialize(profiles);
            }, "list_profiles", "List all profiles."),

            // ── Roots ──
            AIFunctionFactory.Create(async (string rootName, string filesJson, string? profileName) =>
            {
                var files = JsonSerializer.Deserialize<Dictionary<string, string>>(filesJson)
                    ?? throw new ArgumentException("Invalid filesJson");
                var preview = new RootPreview(files);
                await _projectManager.CreateRootAsync(rootName, preview, profileName);
                await _hubContext.Clients.All.RootsChanged();
                return $"Created root '{rootName}' with {files.Count} files.";
            }, "create_root", "Create a root. filesJson is a JSON object mapping file paths to contents (e.g. {\"config.json\": \"...\", \"schema.json\": \"...\", \"scripts/create.sh\": \"...\"}). profileName assigns the root to a profile."),

            AIFunctionFactory.Create(async (string profileName, string rootName) =>
            {
                var preview = await _projectManager.GetRootPreviewAsync(profileName, rootName);
                if (preview == null) return $"Root '{rootName}' not found.";
                return JsonSerializer.Serialize(preview.Files, new JsonSerializerOptions { WriteIndented = true });
            }, "get_root_files", "Get the files in a root's .godmode-root/ directory."),

            AIFunctionFactory.Create(async (string profileName, string rootName, string filesJson) =>
            {
                var files = JsonSerializer.Deserialize<Dictionary<string, string>>(filesJson)
                    ?? throw new ArgumentException("Invalid filesJson");
                await _projectManager.UpdateRootAsync(profileName, rootName, new RootPreview(files));
                await _hubContext.Clients.All.RootsChanged();
                return $"Updated root '{rootName}'.";
            }, "update_root", "Update a root's files. filesJson is a JSON object mapping file paths to contents."),

            AIFunctionFactory.Create(async (string profileName, string rootName) =>
            {
                await _projectManager.DeleteRootAsync(profileName, rootName, force: false);
                await _hubContext.Clients.All.RootsChanged();
                return $"Deleted root '{rootName}'.";
            }, "delete_root", "Delete a root."),

            AIFunctionFactory.Create(async () =>
            {
                var roots = await _projectManager.ListProjectRootsAsync();
                return JsonSerializer.Serialize(roots);
            }, "list_roots", "List all roots with their actions and schemas."),

            // ── MCP Servers ──
            AIFunctionFactory.Create(async (string profileName, string serverName, string command, string? argsJson, string? envJson) =>
            {
                var args = argsJson != null ? JsonSerializer.Deserialize<string[]>(argsJson) : null;
                var env = envJson != null ? JsonSerializer.Deserialize<Dictionary<string, string>>(envJson) : null;
                var config = new McpServerConfig(command, args, env);
                await _projectManager.AddMcpServerAsync(serverName, config, "profile", profileName, null, null);
                await _hubContext.Clients.All.ProfilesChanged();
                return $"Added MCP server '{serverName}' to profile '{profileName}'.";
            }, "add_mcp_server", "Add an MCP server to a profile. argsJson is a JSON array of strings, envJson is a JSON object of key-value pairs."),

            AIFunctionFactory.Create(async (string profileName, string serverName) =>
            {
                await _projectManager.RemoveMcpServerAsync(serverName, "profile", profileName, null, null);
                await _hubContext.Clients.All.ProfilesChanged();
                return $"Removed MCP server '{serverName}' from profile '{profileName}'.";
            }, "remove_mcp_server", "Remove an MCP server from a profile."),

            AIFunctionFactory.Create(async (string profileName, string rootName) =>
            {
                var servers = await _projectManager.GetEffectiveMcpServersAsync(profileName, rootName, null);
                return JsonSerializer.Serialize(servers);
            }, "list_mcp_servers", "List effective MCP servers for a profile/root combination."),

            // ── Projects ──
            AIFunctionFactory.Create(async () =>
            {
                var projects = await _projectManager.ListProjectsAsync();
                return JsonSerializer.Serialize(projects);
            }, "list_projects", "List all projects with their status."),

            AIFunctionFactory.Create(async (string projectId) =>
            {
                var status = await _projectManager.GetStatusAsync(projectId);
                return JsonSerializer.Serialize(status);
            }, "get_project_status", "Get detailed status of a project."),

            AIFunctionFactory.Create(async (string profileName, string rootName, string? actionName, string inputsJson) =>
            {
                var inputs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(inputsJson)
                    ?? new Dictionary<string, JsonElement>();
                var status = await _projectManager.CreateProjectAsync(
                    new CreateProjectRequest(profileName, rootName, inputs, actionName));
                await _hubContext.Clients.All.ProjectCreated(status);
                return $"Created project '{status.Name}' (id: {status.Id}, state: {status.State}).";
            }, "create_project", "Create and start a project from a root. inputsJson is a JSON object with form field values."),

            AIFunctionFactory.Create(async (string projectId) =>
            {
                await _projectManager.StopProjectAsync(projectId);
                return $"Stopped project '{projectId}'.";
            }, "stop_project", "Stop a running project."),

            AIFunctionFactory.Create(async (string projectId, bool force) =>
            {
                await _projectManager.DeleteProjectAsync(projectId, force);
                await _hubContext.Clients.All.ProjectDeleted(projectId);
                return $"Deleted project '{projectId}'.";
            }, "delete_project", "Delete a project. Use force=true to delete a running project."),

            // ── Root generation via LLM ──
            AIFunctionFactory.Create(async (string instruction) =>
            {
                var preview = await _rootGenService.GenerateAsync(new RootGenerationRequest(instruction));
                if (preview.ValidationError != null)
                    return $"Generation failed: {preview.ValidationError}";
                return JsonSerializer.Serialize(preview.Files, new JsonSerializerOptions { WriteIndented = true });
            }, "generate_root_files", "Use LLM to generate root config files from a natural language instruction. Returns the files as JSON — use create_root to save them."),
        ];
    }
}
