namespace GodMode.Voice.Speech;

public interface ISpeechSynthesizer
{
    Task SpeakAsync(string text, CancellationToken ct = default);
    Task StopAsync();
}
