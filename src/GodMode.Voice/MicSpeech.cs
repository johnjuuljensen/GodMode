using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using VoiceBot.Core.Audio;

namespace GodMode.Voice;

/// <summary>
/// Tells the user's speech from quiet in the mic's audio, chunk by chunk, the way VoiceBot's ElevenLabs engine does for
/// its local commit (its <c>SpeechLevel</c>, which is internal to VoiceBot, so measured again here): a chunk is speech
/// when its RMS is at least <see cref="SpeechOverNoise"/> times the noise floor and at least <see cref="MinSpeechRms"/>.
/// The noise floor follows the quietest audio: it drops to any chunk quieter than it at once, and rises towards louder
/// audio by at most <see cref="NoiseRiseDbPerSecond"/>, so steady room noise reads as quiet within seconds.
/// <para>Audio time, not wall-clock: durations come from the samples.</para>
/// </summary>
public sealed class MicSpeech(AudioFormat format)
{
    /// <summary>≈ −44 dBFS: quieter than this is never speech, however quiet the room.</summary>
    public const double MinSpeechRms = 200;

    /// <summary>10 dB over the noise floor.</summary>
    public const double SpeechOverNoise = 3.16;

    public const double NoiseRiseDbPerSecond = 3;

    private double _noise = double.NaN;

    /// <summary>RMS of the noise floor so far; NaN before the first chunk.</summary>
    public double NoiseFloor => _noise;

    /// <summary>Whether a chunk of 16-bit PCM is speech against the floor so far; then updates the floor. The first chunk only sets it.</summary>
    public bool IsSpeech(ReadOnlySpan<byte> pcm16)
    {
        var samples = pcm16.Length / 2;
        if (samples == 0) return false;

        double sum = 0;
        for (var i = 0; i < samples; i++)
        {
            double s = (short)(pcm16[2 * i] | pcm16[2 * i + 1] << 8);
            sum += s * s;
        }
        var rms = Math.Sqrt(sum / samples);
        if (double.IsNaN(_noise))
        {
            _noise = rms;
            return false;
        }

        var speech = rms >= Math.Max(MinSpeechRms, _noise * SpeechOverNoise);
        var seconds = (double)samples / format.Channels / format.SampleRate;
        _noise = rms <= _noise ? rms : Math.Min(rms, Math.Max(_noise, 1) * Math.Pow(10, NoiseRiseDbPerSecond * seconds / 20));
        return speech;
    }
}

/// <summary>
/// A source read as it is, each chunk handed to <c>heard</c> as the session's speech engine takes it: no copy, no pump
/// of its own.
/// </summary>
internal sealed class HeardAudioSource(IAudioSource source, Action<ReadOnlyMemory<byte>> heard) : IAudioSource
{
    public AudioFormat Format => source.Format;
    public ChannelReader<ReadOnlyMemory<byte>> Audio { get; } = new HeardReader(source.Audio, heard);

    private sealed class HeardReader(ChannelReader<ReadOnlyMemory<byte>> inner, Action<ReadOnlyMemory<byte>> heard)
        : ChannelReader<ReadOnlyMemory<byte>>
    {
        public override Task Completion => inner.Completion;

        public override bool TryRead([MaybeNullWhen(false)] out ReadOnlyMemory<byte> item)
        {
            if (!inner.TryRead(out item)) return false;
            heard(item);
            return true;
        }

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
            inner.WaitToReadAsync(cancellationToken);
    }
}
