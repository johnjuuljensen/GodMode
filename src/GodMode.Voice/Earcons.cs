using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using VoiceBot.Core.Audio;

namespace GodMode.Voice;

/// <summary>A short sound before a line, that says what kind of line it is before any word does (#455).</summary>
public enum Earcon
{
    /// <summary>A session needs the user: a question, a permission, a decision, a review. Two notes, rising.</summary>
    Question,

    /// <summary>A session says its work is done. Three notes up a major chord.</summary>
    Done,

    /// <summary>A session failed. Two low notes, falling.</summary>
    Failed,
}

/// <summary>
/// The earcons (#455): which one an attention item's announcement gets, and its sound, made in code in the speaker's PCM
/// format (no files to bundle). Each is a few notes with short fades, and a little silence after, so the words that
/// follow stand apart from it. Distinct from the mic's rising and falling sweeps (<see cref="VoiceMic.Tone"/>).
/// </summary>
public static class Earcons
{
    /// <summary>How loud an earcon is, of full scale: a little under the mic's tones.</summary>
    public const float Volume = 0.12f;

    /// <summary>The silence after an earcon, before the words.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// The earcon an item's announcement is said after: <see cref="Earcon.Question"/> for what holds its session until the
    /// user answers or acts, <see cref="Earcon.Done"/> for a turn the session said is done (#467), <see cref="Earcon.Failed"/>
    /// for an error. None for a turn that ended without saying it is done: idle says nothing of the work.
    /// </summary>
    public static Earcon? For(AttentionItem item) => item.Kind switch
    {
        AttentionKind.Question or AttentionKind.Permission or AttentionKind.Escalation or AttentionKind.Review => Earcon.Question,
        AttentionKind.Error => Earcon.Failed,
        AttentionKind.Finished when item.Outcome == TurnOutcome.Done => Earcon.Done,
        _ => null,
    };

    /// <summary>The notes of each earcon: frequency in Hz, and length.</summary>
    private static IReadOnlyList<(double Hz, int Ms)> Notes(Earcon earcon) => earcon switch
    {
        Earcon.Question => [(659.3, 90), (880.0, 130)],
        Earcon.Done => [(523.3, 80), (659.3, 80), (784.0, 150)],
        Earcon.Failed => [(440.0, 130), (329.6, 220)],
        _ => throw new ArgumentOutOfRangeException(nameof(earcon)),
    };

    /// <summary>The earcon's sound in <paramref name="format"/> (16-bit PCM), with <see cref="Gap"/> of silence after it.</summary>
    public static byte[] Pcm(Earcon earcon, AudioFormat format, float volume = Volume)
    {
        var notes = Notes(earcon);
        var frames = notes.Select(n => n.Ms * format.SampleRate / 1000).ToList();
        var gap = (int)(Gap.TotalSeconds * format.SampleRate);
        var bytes = new byte[(frames.Sum() + gap) * format.Channels * 2];
        var fade = Math.Max(1, format.SampleRate / 100);
        var at = 0;
        for (var n = 0; n < notes.Count; n++)
        {
            for (var i = 0; i < frames[n]; i++, at++)
            {
                var envelope = Math.Min(1.0, Math.Min(i, frames[n] - 1 - i) / (double)fade);
                var value = (short)(Math.Sin(2 * Math.PI * notes[n].Hz * i / format.SampleRate) * envelope * volume * short.MaxValue);
                for (var c = 0; c < format.Channels; c++)
                    BitConverter.TryWriteBytes(bytes.AsSpan((at * format.Channels + c) * 2), value);
            }
        }
        return bytes;
    }
}

/// <summary>
/// The session's speaker, which plays a cue before the next speech (#455): <see cref="Cue"/> sets it, and the first audio
/// sent after is preceded by it, once. The announcement formatter cues an earcon as VoiceBot is about to say the
/// announcement: it formats under the session's graph lock, when nothing is speaking, and starts the speech at once, so
/// the next audio is the announcement's. An interrupt drops a cue not yet played. A cue is any sound in the speaker's
/// format (<see cref="Earcons.Pcm"/>, or another's to play before a line).
/// </summary>
public sealed class CueingSink(IAudioSink speaker) : IAudioSink
{
    private byte[]? _cue;

    public AudioFormat Format => speaker.Format;

    /// <summary>Plays <paramref name="earcon"/> before the next speech; it replaces a cue not played yet.</summary>
    public void Cue(Earcon earcon) => Cue(Earcons.Pcm(earcon, speaker.Format));

    /// <summary>Plays <paramref name="pcm"/>, in <see cref="Format"/>, before the next speech; it replaces a cue not played yet.</summary>
    public void Cue(byte[] pcm) => Volatile.Write(ref _cue, pcm);

    public async Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _cue, null) is { } cue)
            await speaker.SendAudioAsync(cue, ct).ConfigureAwait(false);
        await speaker.SendAudioAsync(audio, ct).ConfigureAwait(false);
    }

    public Task SendStatusAsync(string message, CancellationToken ct) => speaker.SendStatusAsync(message, ct);

    public Task InterruptAsync(CancellationToken ct)
    {
        Volatile.Write(ref _cue, null);
        return speaker.InterruptAsync(ct);
    }
}
