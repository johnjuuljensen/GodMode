using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using VoiceBot.Core.Resources;

namespace GodMode.Voice;

/// <summary>What the bot says itself, not through the model: announcements and the greeting. Danish, else English.</summary>
public sealed class VoicePhrases
{
    private readonly bool _danish;

    public VoicePhrases(SessionLanguages languages) =>
        _danish = languages.Primary.StartsWith("da", StringComparison.OrdinalIgnoreCase);

    public string Greeting => _danish ? "Klar." : "Ready.";

    /// <summary>Before several announcements said together: "3 venter på dig:".</summary>
    public string Several(int count) => _danish ? $"{count} venter på dig:" : $"{count} need you:";

    /// <summary>One project that needs the user, by its handle: short, since the model reads the rest when asked.</summary>
    public string Announce(string handle, AttentionItem item) => (item.Kind, _danish) switch
    {
        (AttentionKind.Question, true) => $"{handle} har et spørgsmål",
        (AttentionKind.Question, false) => $"{handle} has a question",
        (AttentionKind.Permission, true) => $"{handle} skal have tilladelse: {PermissionSummary(item)}. Svar på skærmen",
        (AttentionKind.Permission, false) => $"{handle} needs permission: {PermissionSummary(item)}. Answer it on screen",
        (AttentionKind.Error, true) => $"{handle} fejlede",
        (AttentionKind.Error, false) => $"{handle} failed",
        (AttentionKind.Review, true) => $"{handle} har fået ændringsønsker",
        (AttentionKind.Review, false) => $"{handle} has changes requested",
        (AttentionKind.Finished, true) => $"{handle} er færdig",
        (AttentionKind.Finished, false) => $"{handle} is done",
    };

    private static string PermissionSummary(AttentionItem item) => item.Permission?.Summary ?? item.Text;
}
