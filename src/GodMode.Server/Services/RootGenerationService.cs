using System.Text.Json;
using GodMode.AI;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Orchestrates LLM-assisted root generation and modification.
/// Uses InferenceRouter to call the LLM with root-specific system prompts.
/// </summary>
public class RootGenerationService
{
    private readonly InferenceRouter _inference;
    private readonly ILogger<RootGenerationService> _logger;
    private bool _initialized;

    private const string SystemPrompt = """
        You are an expert at creating GodMode root configurations. A "root" defines how Claude Code projects are created.

        ## Root Structure
        A root lives in a `.godmode-root/` directory and contains:
        - `config.json` — Base config with: description, profileName, environment (dict), claudeArgs (string[]), prepare (script path), delete (script path), model
        - `config.{actionName}.json` — Per-action overlay. Merged with base: scalars replace, dicts merge, claudeArgs concatenate. Contains: description, model, scriptsCreateFolder, create (script path), nameTemplate, promptTemplate
        - `{actionName}/schema.json` — JSON Schema for the action's input form. Supported field types: string, string with x-multiline:true, boolean, enum. Required fields in "required" array.
        - `{actionName}/create.sh` and `{actionName}/create.ps1` — Cross-platform create scripts
        - `scripts/prepare.sh` and `scripts/prepare.ps1` — One-time setup scripts
        - `scripts/delete.sh` and `scripts/delete.ps1` — Cleanup scripts

        ## Script Environment Variables
        Scripts receive these environment variables:
        - GODMODE_ROOT_PATH — Root directory path
        - GODMODE_PROJECT_PATH — Project directory path
        - GODMODE_PROJECT_ID — Unique project ID
        - GODMODE_PROJECT_NAME — User-entered project name
        - GODMODE_RESULT_FILE — Path to result file (scripts can override project_path and project_name)
        - GODMODE_INPUT_{KEY} — Form input values (camelCase key converted to UPPER_SNAKE_CASE, e.g., repoUrl becomes GODMODE_INPUT_REPO_URL)
        - GODMODE_FORCE — "true" when force-deleting

        ## Templates
        - nameTemplate: Uses {fieldName} placeholders, e.g., "issue_{issueNumber}"
        - promptTemplate: Uses {fieldName} placeholders, e.g., "Read issue #{issueNumber} and implement"

        ## Script Conventions
        - Always provide BOTH .sh and .ps1 versions
        - .sh scripts: Start with #!/bin/bash and set -e
        - .ps1 scripts: Start with $ErrorActionPreference = 'Stop'
        - Use $env:VAR_NAME in PowerShell, $VAR_NAME in bash
        - Scripts are run from the ROOT directory, not the project directory
        - Script paths in config.json are relative to .godmode-root/ (e.g., "create": "freeform/create")
        - Script references are extensionless — the runtime resolves .sh or .ps1 based on platform

        ## Your Response Format
        Return a JSON object where keys are file paths (relative to .godmode-root/) and values are file contents.
        Example:
        {
          "config.json": "{ ... }",
          "config.freeform.json": "{ ... }",
          "freeform/schema.json": "{ ... }",
          "freeform/create.sh": "#!/bin/bash\nset -e\n...",
          "freeform/create.ps1": "$ErrorActionPreference = 'Stop'\n..."
        }

        Return ONLY the JSON object, no markdown fences, no explanation.
        """;

    public RootGenerationService(InferenceRouter inference, ILogger<RootGenerationService> logger)
    {
        _inference = inference;
        _logger = logger;
    }

    /// <summary>
    /// Generates or modifies root files using LLM assistance.
    /// </summary>
    public async Task<RootPreview> GenerateAsync(RootGenerationRequest request, CancellationToken ct = default)
    {
        if (!_initialized)
        {
            await _inference.InitializeAsync();
            _initialized = true;
        }

        if (!_inference.IsLoaded)
        {
            return new RootPreview(new Dictionary<string, string>(),
                "No inference provider available. Configure an API key in ~/.godmode/inference.json");
        }

        var userMessage = BuildUserMessage(request);

        _logger.LogInformation("Generating root config via LLM ({Provider})", _inference.LastUsedProvider ?? "unknown");
        var response = await _inference.GenerateAsync(InferenceTier.Heavy, SystemPrompt, userMessage, ct);

        if (string.IsNullOrWhiteSpace(response))
        {
            return new RootPreview(new Dictionary<string, string>(),
                "LLM returned empty response. Check inference configuration.");
        }

        try
        {
            // Strip markdown fences if present
            response = response.Trim();
            if (response.StartsWith("```"))
            {
                var firstNewline = response.IndexOf('\n');
                response = response[(firstNewline + 1)..];
                if (response.EndsWith("```"))
                    response = response[..^3];
                response = response.Trim();
            }

            var files = JsonSerializer.Deserialize<Dictionary<string, string>>(response);
            if (files == null || files.Count == 0)
                return new RootPreview(new Dictionary<string, string>(), "LLM response did not contain valid file definitions");

            return new RootPreview(files);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse LLM response as JSON");
            return new RootPreview(new Dictionary<string, string>(),
                $"Failed to parse LLM response: {ex.Message}");
        }
    }

    private static string BuildUserMessage(RootGenerationRequest request)
    {
        var parts = new List<string>();

        if (request.CurrentFiles is { Count: > 0 })
        {
            parts.Add("Here are the current root files:\n");
            foreach (var (path, content) in request.CurrentFiles)
                parts.Add($"--- {path} ---\n{content}\n");
            parts.Add("\nModify these files based on the following instruction:");
        }
        else
        {
            parts.Add("Create a new root configuration from scratch based on the following instruction:");
        }

        parts.Add(request.UserInstruction);

        if (request.SchemaFields is { Length: > 0 })
        {
            parts.Add("\nThe user has already defined these input form fields in the schema editor:");
            foreach (var field in request.SchemaFields)
            {
                var desc = $"- {field.Key} ({field.FieldType}): \"{field.Title}\"";
                if (field.IsRequired) desc += " [required]";
                if (field.EnumValues is { Length: > 0 }) desc += $" options: [{string.Join(", ", field.EnumValues)}]";
                parts.Add(desc);
            }
            parts.Add("Use these fields in the schema.json and reference them in scripts as GODMODE_INPUT_{UPPER_SNAKE_CASE_KEY}.");
        }

        return string.Join("\n", parts);
    }
}
