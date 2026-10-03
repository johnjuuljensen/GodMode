using System.Text.Json.Serialization;

namespace GodMode.Shared.Enums;

/// <summary>
/// How much a session may interrupt the user (issue #438), least first: its <c>settings.json</c>'s <c>importance</c>, set at
/// its create from its action's <c>importance</c> (else <see cref="Normal"/>), and by the user since
/// (<see cref="Hubs.IProjectHub.SetImportance"/>). Every item stays in the user's list whatever it is: it decides only
/// how loudly an item is brought to the user (<see cref="AttentionAlert"/>). General enough for what is not a session
/// (notices from outside services, #439). <see cref="Normal"/> is 0, the default, so the JSON leaves it out, and a
/// session or server from before tiers reads as normal.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<Importance>))]
public enum Importance
{
    /// <summary>
    /// The inbox only: its result, review and error notify nobody. What blocks it (a permission prompt, a question, an
    /// escalation) still notifies, as <see cref="Normal"/>'s does.
    /// </summary>
    Quiet = -1,

    /// <summary>What every session did before tiers: each item is in the inbox, a notification on the phone, and said by voice.</summary>
    Normal = 0,

    /// <summary>Each item interrupts too: a sound, the phone's heads-up notification, and voice says it before the others.</summary>
    Important = 1,
}
