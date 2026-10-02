using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GodMode.HeadsetSpike;

/// <summary>
/// The short, quiet tone step 3 plays when the mic is open, and its falling twin for step 4: a sine sweep with a
/// fade in and out, made locally (no speech synthesis) in the speaker's own mix format and played through WASAPI
/// shared mode. Its start and end are logged, so its offset from the mic's open reads off the log.
/// </summary>
public sealed class Tone(SpikeLog log)
{
    private const string Source = "TONE";

    public static readonly TimeSpan DefaultLength = TimeSpan.FromMilliseconds(80);

    /// <param name="endpointId">The speaker; null for the default of <paramref name="role"/>.</param>
    /// <remarks>On a background (MTA) thread, as every Core Audio object here: NAudio's fail across apartments.</remarks>
    public Task PlayAsync(bool rising, TimeSpan length, float volume, string? endpointId, Role role = Role.Multimedia) =>
        Task.Run(() => PlayHereAsync(rising, length, volume, endpointId, role));

    private async Task PlayHereAsync(bool rising, TimeSpan length, float volume, string? endpointId, Role role)
    {
        var name = rising ? "rising" : "falling";
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = endpointId is null ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role) : enumerator.GetDevice(endpointId);
            WaveFormat mix;
            using (var probe = device.AudioClient) mix = probe.MixFormat;
            var samples = Sweep(rising, length, volume, mix.SampleRate, mix.Channels);
            var format = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);
            using var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 20);
            var done = new TaskCompletionSource();
            output.PlaybackStopped += (_, e) =>
            {
                if (e.Exception is { } ex) log.Error(Source, "playback", ex);
                done.TrySetResult();
            };
            output.Init(new RawSourceWaveStream(ToBytes(samples), format).ToSampleProvider());
            log.Write(Source, $"{name} {length.TotalMilliseconds:F0} ms at {volume:P0} on '{device.FriendlyName}' ({mix.SampleRate} Hz, {mix.Channels} ch): start");
            output.Play();
            await done.Task.WaitAsync(length + TimeSpan.FromSeconds(2));
            log.Write(Source, $"{name}: ended");
        }
        catch (Exception ex)
        {
            log.Error(Source, $"{name} tone", ex);
        }
    }

    /// <summary>A sweep between 660 and 990 Hz, up or down, with 10 ms fades, on every channel.</summary>
    public static float[] Sweep(bool rising, TimeSpan length, float volume, int sampleRate, int channels)
    {
        var frames = (int)(length.TotalSeconds * sampleRate);
        var fade = Math.Max(1, sampleRate / 100);
        var samples = new float[frames * channels];
        double phase = 0;
        for (var i = 0; i < frames; i++)
        {
            var t = (double)i / frames;
            var frequency = rising ? 660 + 330 * t : 990 - 330 * t;
            phase += 2 * Math.PI * frequency / sampleRate;
            var envelope = Math.Min(1.0, Math.Min(i, frames - 1 - i) / (double)fade);
            var value = (float)(Math.Sin(phase) * envelope * volume);
            for (var c = 0; c < channels; c++) samples[i * channels + c] = value;
        }
        return samples;
    }

    private static MemoryStream ToBytes(float[] samples)
    {
        var bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return new MemoryStream(bytes);
    }
}
