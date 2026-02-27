using GodMode.Voice.Speech;
using Windows.Media.SpeechSynthesis;
using Windows.Media.Playback;
using Windows.Media.Core;

namespace GodMode.Voice.Windows;

public sealed class WindowsSpeechSynthesizer : ISpeechSynthesizer
{
    private readonly SpeechSynthesizer _synth = new();
    private MediaPlayer? _player;

    public async Task SpeakAsync(string text, CancellationToken ct = default)
    {
        await StopAsync();

        var stream = await _synth.SynthesizeTextToStreamAsync(text).AsTask(ct);
        _player = new MediaPlayer();
        _player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);

        var tcs = new TaskCompletionSource();
        _player.MediaEnded += (s, e) => tcs.TrySetResult();
        _player.MediaFailed += (s, e) => tcs.TrySetResult();

        using var registration = ct.Register(() => tcs.TrySetCanceled());

        _player.Play();
        await tcs.Task;
    }

    public Task StopAsync()
    {
        if (_player is not null)
        {
            _player.Pause();
            _player.Dispose();
            _player = null;
        }
        return Task.CompletedTask;
    }
}
