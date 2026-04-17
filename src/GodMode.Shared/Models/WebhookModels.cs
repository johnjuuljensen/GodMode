using System.Text.Json;

namespace GodMode.Shared.Models;

/// <summary>
/// Full webhook configuration stored on disk as .webhooks/{keyword}.json.
/// </summary>
public sealed record WebhookConfig(
    string Token,
    string ProfileName,
    string RootName,
    string? ActionName = null,
    string? Description = null,
    Dictionary<string, string>? InputMapping = null,
    Dictionary<string, JsonElement>? StaticInputs = null,
    bool Enabled = true);

/// <summary>
/// Wire type for listing webhooks — token is redacted to a prefix.
/// </summary>
public sealed record WebhookInfo(
    string Keyword,
    string ProfileName,
    string RootName,
    string? ActionName = null,
    string? Description = null,
    bool Enabled = true,
    string? TokenPrefix = null);

/// <summary>
/// Response returned when a webhook triggers project creation.
/// </summary>
public sealed record WebhookResult(
    string ProjectId,
    string ProjectName,
    string Status);
