namespace GodMode.Server.Services;

/// <summary>Cutting text to a length without splitting a character that takes two UTF-16 units (an emoji).</summary>
public static class TextCut
{
    /// <summary>
    /// The length to cut <paramref name="text"/> to so that it is at most <paramref name="max"/>: one less
    /// when the cut would fall between a surrogate pair's halves, which a phone shows as U+FFFD.
    /// </summary>
    public static int SafeLength(string text, int max) =>
        max >= text.Length ? text.Length
        : max <= 0 ? 0
        : char.IsHighSurrogate(text[max - 1]) ? max - 1
        : max;

    /// <summary><paramref name="text"/> cut to at most <paramref name="max"/> characters, never inside a surrogate pair.</summary>
    public static string Cut(string text, int max) => text[..SafeLength(text, max)];
}
