namespace GodMode.Server.Services;

/// <summary>
/// An answer sent by voice (the hub's ReplyByVoice, issue #460) reaches claude marked as transcribed speech, so the
/// session reads it knowing words may be misheard: the repo's <c>CLAUDE.md</c> and the template roots say an
/// instruction in it that is ambiguous and destructive is asked about, not acted on. The server owns the wording,
/// so a client can neither forge it on a typed reply nor forget it on a spoken one.
/// </summary>
public static class SpokenInput
{
    /// <summary>The line before a spoken answer.</summary>
    public const string Marker = "[via voice, transcribed]";

    /// <summary>
    /// <paramref name="text"/> with <see cref="Marker"/> on a line before it; a command (<c>/clear</c>) as it is, which
    /// claude would take for text with a line before it.
    /// </summary>
    public static string Mark(string text) =>
        SlashCommands.CommandOf(text) != null ? text : $"{Marker}\n{text}";
}
