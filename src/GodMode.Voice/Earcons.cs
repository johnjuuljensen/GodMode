using System.Runtime.CompilerServices;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using Microsoft.Extensions.Logging;
using VoiceBot.Core.Audio;
using VoiceBot.Core.Pipeline;

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

    /// <summary>The earcon a create's outcome is said after (#523): <see cref="Earcon.Done"/> when it was made, <see cref="Earcon.Failed"/> when not.</summary>
    public static Earcon For(CreateOutcome outcome) => outcome.Error is null ? Earcon.Done : Earcon.Failed;

    /// <summary>The notes of each earcon: frequency in Hz, and length.</summary>
    private static IReadOnlyList<(double Hz, int Ms)> Notes(Earcon earcon) => earcon switch
    {
        Earcon.Question => [(659.3, 90), (880.0, 130)],
        Earcon.Done => [(523.3, 80), (659.3, 80), (784.0, 150)],
        Earcon.Failed => [(440.0, 130), (329.6, 220)],
        _ => throw new ArgumentOutOfRangeException(nameof(earcon)),
    };

    /// <summary>The earcon's sound in <paramref name="format"/> (16-bit PCM), with <see cref="Gap"/> of silence after it.</summary>
    public static byte[] Pcm(Earcon earcon, AudioFormat format, float volume = Volume) => Render(Notes(earcon), Gap, format, volume);

    /// <summary>How loud the "heard you" tone is, of full scale: quieter than the earcons, as it plays on every turn.</summary>
    public const float HeardVolume = 0.08f;

    /// <summary>
    /// The "heard you" tone (#458), played as a final is taken as a turn: one short note, higher than the earcons' and
    /// out of the mic's sweeps (<see cref="VoiceMic.Tone"/>), so it reads as neither. No gap: nothing follows it at once.
    /// </summary>
    public static byte[] Heard(AudioFormat format, float volume = HeardVolume) => Render([(1174.7, 70)], TimeSpan.Zero, format, volume);

    /// <summary><paramref name="notes"/> one after the other, each with short fades, then <paramref name="gap"/> of silence.</summary>
    private static byte[] Render(IReadOnlyList<(double Hz, int Ms)> notes, TimeSpan gap, AudioFormat format, float volume)
    {
        var frames = notes.Select(n => n.Ms * format.SampleRate / 1000).ToList();
        var silence = (int)(gap.TotalSeconds * format.SampleRate);
        var bytes = new byte[(frames.Sum() + silence) * format.Channels * 2];
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
/// The earcons of announcements that are of no attention item (#523): a create's outcome, which has its earcon all the
/// same (<see cref="Earcons.For(CreateOutcome)"/>). Kept by the announcement itself, as the board keeps its own
/// (<see cref="AttentionBoard.AnnouncementOf"/>): two of the same words are two announcements.
/// </summary>
public sealed class AnnouncementEarcons
{
    private readonly ConditionalWeakTable<Announcement, object> _earcons = new();

    /// <summary><paramref name="announcement"/>, said after <paramref name="earcon"/>.</summary>
    public Announcement Cued(Announcement announcement, Earcon earcon)
    {
        _earcons.AddOrUpdate(announcement, earcon);
        return announcement;
    }

    /// <summary>The earcon <paramref name="announcement"/> was given (<see cref="Cued"/>); null for none.</summary>
    public Earcon? Of(Announcement announcement) => _earcons.TryGetValue(announcement, out var earcon) ? (Earcon)earcon : null;
}

/// <summary>
/// The session's speaker, which plays a cue before the next speech (#455): <see cref="Cue"/> sets it, and the first audio
/// sent after is preceded by it, once. The announcement formatter cues an earcon as VoiceBot is about to say the
/// announcement: it formats under the session's graph lock, when nothing is speaking, and starts the speech at once, so
/// the next audio is the announcement's. An interrupt drops a cue not yet played, and so does the start of the speech
/// after the one it was for (<see cref="SpeechStarted"/>, #523): that speech made no audio (its synthesis failed), and
/// the cue is not the next one's. A cue is any sound in the speaker's format (<see cref="Earcons.Pcm"/>, or another's to
/// play before a line).
/// <para>
/// It also plays a sound now, between lines (<see cref="Heard"/>, #458): only when the speaker is quiet, so it never
/// plays over speech, nor speech over it. Quiet is reckoned here, from what was sent: each send plays from when it is
/// sent, or when the audio before it ends, in real time (as VoiceBot reckons its own speech), and an interrupt ends what
/// was sent. Sends are one at a time, so speech that comes while the sound is sent plays after it.
/// </para>
/// </summary>
public sealed class CueingSink(IAudioSink speaker, ILogger? logger = null, TimeProvider? time = null) : IAudioSink
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly Lock _lock = new();
    private byte[]? _cue;
    private bool _cueSpeechStarted;
    private long _playsUntil;

    public AudioFormat Format => speaker.Format;

    /// <summary>Plays <paramref name="earcon"/> before the next speech; it replaces a cue not played yet.</summary>
    public void Cue(Earcon earcon) => Cue(Earcons.Pcm(earcon, speaker.Format));

    /// <summary>Plays <paramref name="pcm"/>, in <see cref="Format"/>, before the next speech; it replaces a cue not played yet.</summary>
    public void Cue(byte[] pcm)
    {
        lock (_lock)
        {
            _cue = pcm;
            _cueSpeechStarted = false;
        }
    }

    /// <summary>
    /// A speech starts (VoiceBot's response, before its first audio): the first after a cue is the one it is for. Any
    /// later one drops a cue still not played (#523): the speech it was for made no audio, and it is not this one's.
    /// </summary>
    public void SpeechStarted()
    {
        lock (_lock)
        {
            if (_cue is null)
                return;
            if (_cueSpeechStarted)
            {
                _cue = null;
                logger?.LogInformation("Voice: a cue was dropped: the speech it was for made no audio");
            }
            _cueSpeechStarted = true;
        }
    }

    /// <summary>
    /// The "heard you" tone (<see cref="Earcons.Heard"/>) now, when the speaker is quiet: nothing playing, nothing being
    /// sent, no cue waiting for its line; nothing otherwise. Returns at once, the tone's send started: speech sent after
    /// it plays after it.
    /// </summary>
    public void Heard() => _ = PlayIfQuietAsync(Earcons.Heard(speaker.Format));

    /// <summary>
    /// Plays <paramref name="pcm"/>, in <see cref="Format"/>, now, when the speaker is quiet (<see cref="Heard"/>); whether
    /// it did. Takes the send before its first await, so what is sent after it was called waits for it. Never throws.
    /// </summary>
    public async Task<bool> PlayIfQuietAsync(byte[] pcm)
    {
        if (!_sending.Wait(0))
            return false;
        try
        {
            if (Volatile.Read(ref _cue) is not null || !Quiet)
                return false;
            await SendHeldAsync(pcm, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Voice: a sound did not play");
            return false;
        }
        finally
        {
            _sending.Release();
        }
    }

    /// <summary>Nothing sent is still playing, as reckoned from what was sent.</summary>
    private bool Quiet
    {
        get
        {
            lock (_lock) return _time.GetTimestamp() >= _playsUntil;
        }
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct)
    {
        await _sending.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _cue, null) is { } cue)
                await SendHeldAsync(cue, ct).ConfigureAwait(false);
            await SendHeldAsync(audio, ct).ConfigureAwait(false);
        }
        finally
        {
            _sending.Release();
        }
    }

    /// <summary>Sends <paramref name="audio"/>, holding the send, and reckons when it ends playing.</summary>
    private async Task SendHeldAsync(ReadOnlyMemory<byte> audio, CancellationToken ct)
    {
        await speaker.SendAudioAsync(audio, ct).ConfigureAwait(false);
        var length = (long)(audio.Length / (double)speaker.Format.BytesPerSecond * _time.TimestampFrequency);
        lock (_lock) _playsUntil = Math.Max(_time.GetTimestamp(), _playsUntil) + length;
    }

    public Task SendStatusAsync(string message, CancellationToken ct) => speaker.SendStatusAsync(message, ct);

    public Task InterruptAsync(CancellationToken ct)
    {
        Volatile.Write(ref _cue, null);
        lock (_lock) _playsUntil = 0;
        return speaker.InterruptAsync(ct);
    }
}
