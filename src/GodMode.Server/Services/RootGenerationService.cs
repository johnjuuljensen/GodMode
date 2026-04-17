using System.Text.Json;
using GodMode.AI;
using GodMode.Shared.Models;
using Microsoft.Extensions.AI;

namespace GodMode.Server.Services;

/// <summary>
/// Generates root configurations from natural language using the inference system.
/// </summary>
public class RootGenerationService
{
    private readonly InferenceRouter _inferenceRouter;
    private readonly ILogger<RootGenerationService> _logger;

    private const string SystemPrompt = """
        You are a GodMode root configuration generator. Given a user's description,
        generate the files needed for a .godmode-root/ directory.

        ## Root directory structure
        A root contains a .godmode-root/ folder with:
        - config.json (required): Base configuration
        - config.{action}.json (optional): Per-action overlays
        - schema.json or {action}/schema.json: Input schema for the UI form
        - scripts/*.sh or scripts/*.ps1: Prepare/create/delete scripts

        ## config.json format
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
          "model": null
        }

        ## schema.json format (JSON Schema)
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "title": "Project Name" },
            "prompt": { "type": "string", "title": "Task Description", "x-multiline": true },
            "skipPermissions": { "type": "boolean", "title": "Skip Permissions", "default": "true" }
          },
          "required": ["name", "prompt"]
        }

        ## Script environment variables
        Scripts receive: GODMODE_ROOT_PATH, GODMODE_PROJECT_PATH, GODMODE_PROJECT_ID,
        GODMODE_PROJECT_NAME, GODMODE_RESULT_FILE, and GODMODE_INPUT_* for each form field.

        ## Server deployment constraints
        Scripts run inside a Docker container on cloud platforms (Azure Container Apps, AWS ECS, Railway).
        The workspace is mounted from network storage (Azure Files, EFS, etc.). Follow these rules:

        1. **No chmod** — Network-mounted filesystems (Azure Files, NFS) don't support permission changes.
           Never use `chmod` in scripts. If you must, guard it: `chmod +x file 2>/dev/null || true`
        2. **No sudo** — The container runs as a non-root user. Never use sudo.
        3. **No systemd/service commands** — No systemctl, service, etc.
        4. **No package installation** — Don't use apt-get, yum, brew, etc. The container image is immutable.
           Only npm/npx are available (Node.js is pre-installed).
        5. **Portable paths** — Use $GODMODE_PROJECT_PATH, never hardcode paths.
        6. **No interactive commands** — Scripts run headlessly. No prompts, no editors.
        7. **Idempotent scripts** — Scripts may run multiple times (project reuse). Use mkdir -p, don't fail if files exist.
        8. **Keep scripts simple** — Create directories, write template files, initialize git. Don't do complex setups.
        9. **set -e** — Always start bash scripts with `set -e` to fail fast on errors.
        10. **mkdir syntax** — Use `mkdir -p dir1 dir2 dir3`, NOT `mkdir -p {dir1,dir2}` (brace expansion is fragile).

        ## claudeArgs rules
        The `claudeArgs` field in config.json passes extra flags to the Claude Code CLI.
        - Only use valid Claude Code CLI flags: `--model`, `--verbose`, `--allowedTools`
        - NEVER use `--mcp` or `--mcp-config` — MCP servers are injected automatically by GodMode
        - Leave `claudeArgs` as an empty array `[]` unless you have a specific reason
        - When in doubt, omit the field entirely

        ## Cross-platform scripts
        For server deployments, only .sh scripts are needed (servers run Linux).
        Provide .ps1 variants only if the root is specifically for Windows/local use.

        ## Response format
        Return ONLY a JSON object mapping file paths to contents.
        Example: {"config.json": "...", "schema.json": "...", "scripts/create.sh": "..."}
        """;

    public RootGenerationService(InferenceRouter inferenceRouter, ILogger<RootGenerationService> logger)
    {
        _inferenceRouter = inferenceRouter;
        _logger = logger;
    }

    public async Task<RootPreview> GenerateAsync(RootGenerationRequest request, CancellationToken ct = default)
    {
        if (!_inferenceRouter.IsLoaded)
            throw new InvalidOperationException("Inference is not available. Configure an API key or local model.");

        var userMessage = BuildUserMessage(request);

        _logger.LogInformation("Generating root from instruction: {Instruction}", request.Instruction);

        // Root generation produces multi-file JSON — needs high token limit
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, userMessage)
        };
        var options = new ChatOptions { MaxOutputTokens = 8192, Temperature = 0.3f };
        var chatResponse = await _inferenceRouter.GenerateAsync(InferenceTier.Heavy, messages, options, ct);
        var response = chatResponse?.Text ?? "";

        // Parse the JSON response
        try
        {
            // Try to extract JSON from markdown code blocks if present
            var json = response;
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
                json = response[jsonStart..(jsonEnd + 1)];

            var files = JsonSerializer.Deserialize<Dictionary<string, string>>(json,
                new JsonSerializerOptions { AllowTrailingCommas = true })
                ?? throw new InvalidOperationException("LLM returned empty response.");

            return new RootPreview(files);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse LLM response as JSON");
            return new RootPreview(
                new Dictionary<string, string>(),
                ValidationError: $"Failed to parse LLM response: {ex.Message}. Raw response: {response[..Math.Min(500, response.Length)]}");
        }
    }

    private static string BuildUserMessage(RootGenerationRequest request)
    {
        var parts = new List<string> { $"Create a GodMode root configuration for: {request.Instruction}" };

        if (request.CurrentFiles is { Count: > 0 })
        {
            parts.Add("\nExisting files to modify/extend:");
            foreach (var (path, content) in request.CurrentFiles)
                parts.Add($"\n--- {path} ---\n{content}");
        }

        if (request.SchemaFields is { Length: > 0 })
        {
            parts.Add($"\nThe form should include these fields: {string.Join(", ", request.SchemaFields)}");
        }

        return string.Join("\n", parts);
    }
}
