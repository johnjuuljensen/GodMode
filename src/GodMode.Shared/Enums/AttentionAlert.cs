using System.Text.Json.Serialization;

namespace GodMode.Shared.Enums;

/// <summary>
/// How loudly an attention item is brought to the user (issue #438), from its session's <see cref="Importance"/> and its
/// <see cref="AttentionKind"/>; least first. The server decides it, so every client and voice agree. Whether a device
/// makes a sound at all is the device's own setting.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AttentionAlert>))]
public enum AttentionAlert
{
    /// <summary>In the inbox, and nowhere else: no notification, and voice does not announce it.</summary>
    Inbox,

    /// <summary>Also a notification on the phone, and voice announces it.</summary>
    Notify,

    /// <summary>Also interrupts: a sound, the phone's heads-up notification, and voice announces it before the others.</summary>
    Interrupt,
}
