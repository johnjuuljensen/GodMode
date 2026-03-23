using System.Text.Json;

namespace GodMode.Maui.Bridge;

/// <summary>
/// Envelope for all messages between the MAUI host and the React app.
/// Payloads use GodMode.Shared types serialized with JsonDefaults.
/// </summary>
/// <param name="Type">Message type discriminator (e.g., "host.ready", "voice.transcript").</param>
/// <param name="Id">Correlation ID for request/response pairs. Null for fire-and-forget events.</param>
/// <param name="Payload">Typed payload serialized with JsonDefaults conventions (PascalCase, string enums).</param>
public record BridgeMessage(string Type, string? Id = null, JsonElement? Payload = null);
