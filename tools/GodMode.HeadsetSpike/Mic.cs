using VoiceBot.Core.Audio;
using VoiceBot.Providers.Windows;

namespace GodMode.HeadsetSpike;

/// <summary>
/// The microphone, opened as GodMode.Maui's voice opens it without echo cancellation: VoiceBot's NativeAudioSource with
/// <see cref="MicCapture.WaveIn"/>, 16 kHz mono in 100 ms buffers, on a Core Audio endpoint. Opening the headset's
/// microphone is what moves it from A2DP to HFP, so the open is the reference the switch is timed from: the first
/// buffer, and the first one with sound in it (a Bluetooth link not up yet gives silence first), are logged after it.
/// </summary>
public sealed class Mic(SpikeLog log)
{
    private const string Source = "MIC";
    private const short SoundFloor = 200; // of 32767: above a silent link's zeros and dither
    private readonly SemaphoreSlim _switching = new(1, 1);
    private volatile NativeAudioSource? _source;
    private CancellationTokenSource? _reading;

    public bool IsOpen => _source is not null;
    public string? EndpointId { get; private set; }
    public double Level { get; private set; }

    /// <summary>The first buffer with sound in it after the open: a tone that waits for this is not cut by the switch.</summary>
    public event Action? FirstSound;

    /// <summary>
    /// Opens it on a worker thread: waveInOpen blocks while the headset switches to HFP, and the UI thread must keep
    /// running the keyboard hook and Mark meanwhile. The reference is the moment it was asked for; the log says when
    /// the open returned. Opens and closes run one at a time, in the order asked.
    /// </summary>
    public Task OpenAsync(string? endpointId)
    {
        log.Reference("mic open", $"asked, {endpointId ?? "default"}");
        return Serialized(() =>
        {
            if (_source is not null) return;
            var asked = log.Now;
            NativeAudioSource? source = null;
            try
            {
                source = new NativeAudioSource(capture: MicCapture.WaveIn, endpointId: endpointId);
                source.Start();
                log.Write(Source, $"open returned: WaveIn started ({source.Description}), {(log.Now - asked).TotalMilliseconds:F0} ms after it began");
                _source = source;
                EndpointId = endpointId;
                _reading = new CancellationTokenSource();
                _ = ReadAsync(source, _reading.Token);
            }
            catch (Exception ex)
            {
                source?.Dispose();
                log.Error(Source, "open", ex);
            }
        });
    }

    private async Task Serialized(Action act)
    {
        await _switching.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(act).ConfigureAwait(false);
        }
        finally
        {
            _switching.Release();
        }
    }

    private async Task ReadAsync(NativeAudioSource source, CancellationToken ct)
    {
        var buffers = 0;
        var heard = false;
        short secondPeak = 0;
        try
        {
            await foreach (var chunk in source.Audio.ReadAllAsync(ct))
            {
                var peak = Peak(chunk.Span);
                Level = peak / 32768.0;
                if (buffers++ == 0) log.Write(Source, $"first buffer ({chunk.Length} bytes, peak {peak})");
                // Once a second (10 buffers), the loudest sample in it: shows whether the mic hears anything at all
                secondPeak = Math.Max(secondPeak, peak);
                if (buffers % 10 == 0)
                {
                    log.Write(Source, $"level: peak {secondPeak} of 32767 in the last second (sound floor {SoundFloor})");
                    secondPeak = 0;
                }
                if (!heard && peak > SoundFloor)
                {
                    heard = true;
                    log.Write(Source, $"first sound (buffer {buffers}, peak {peak})");
                    FirstSound?.Invoke();
                }
            }
            log.Write(Source, $"capture ended after {buffers} buffers");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            log.Error(Source, "capture", ex);
        }
    }

    private static short Peak(ReadOnlySpan<byte> pcm16)
    {
        var peak = 0;
        for (var i = 0; i + 1 < pcm16.Length; i += 2)
            peak = Math.Max(peak, Math.Abs((int)(short)(pcm16[i] | (pcm16[i + 1] << 8))));
        return (short)Math.Min(peak, short.MaxValue);
    }

    /// <summary>Closes it on a worker thread, as <see cref="OpenAsync"/> opens it: the reference is the moment it was asked for.</summary>
    public Task CloseAsync()
    {
        log.Reference("mic close", "asked");
        return Serialized(() =>
        {
            if (_source is not { } source) return;
            _reading?.Cancel();
            var asked = log.Now;
            try
            {
                source.Stop();
                source.Dispose();
                log.Write(Source, $"close returned: WaveIn stopped and closed, {(log.Now - asked).TotalMilliseconds:F0} ms after it began");
            }
            catch (Exception ex)
            {
                log.Error(Source, "close", ex);
            }
            _source = null;
            EndpointId = null;
            Level = 0;
        });
    }

    /// <summary>The format the stack records in, for the log's header.</summary>
    public static AudioFormat Format => AudioFormat.Pcm16kHz;
}
