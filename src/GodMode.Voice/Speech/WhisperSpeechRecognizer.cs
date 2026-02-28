using Whisper.net;

namespace GodMode.Voice.Speech;

public sealed class WhisperSpeechRecognizer : ISpeechRecognizer, IDisposable
{
    private WhisperProcessor? _processor;
    private string? _modelPath;

    public string EngineName => "Whisper.net";

    public event EventHandler<string>? PartialResultReceived;

    public IReadOnlyList<string> GetAvailableLanguages() => ["en-US"];

    public Task<bool> IsAvailableAsync()
    {
        return Task.FromResult(_processor is not null || (_modelPath is not null && File.Exists(_modelPath)));
    }

    public async Task InitializeAsync(string modelPath)
    {
        _modelPath = modelPath;
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Whisper model not found at: {modelPath}");

        await Task.Run(() =>
        {
            var factory = WhisperFactory.FromPath(modelPath);
            _processor = factory.CreateBuilder()
                .WithLanguage("en")
                .Build();
        });
    }

    public Task<string> RecognizeSpeechAsync(CancellationToken ct = default)
    {
        if (_processor is null)
            throw new InvalidOperationException("Whisper not initialized. Call InitializeAsync first.");

        // WhisperSpeechRecognizer requires audio data — the actual mic capture
        // is handled at the platform layer. Use RecognizeFromFileAsync instead.
        throw new NotImplementedException(
            "WhisperSpeechRecognizer requires audio data. " +
            "Use RecognizeFromFileAsync or provide audio through the platform audio capture layer.");
    }

    public async Task<string> RecognizeFromFileAsync(string wavFilePath, CancellationToken ct = default)
    {
        if (_processor is null)
            throw new InvalidOperationException("Whisper not initialized. Call InitializeAsync first.");

        if (!File.Exists(wavFilePath))
            throw new FileNotFoundException($"Audio file not found: {wavFilePath}");

        var segments = new List<string>();

        using var fileStream = File.OpenRead(wavFilePath);

        await foreach (var segment in _processor.ProcessAsync(fileStream, ct))
        {
            var text = segment.Text.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                segments.Add(text);
                PartialResultReceived?.Invoke(this, text);
            }
        }

        return string.Join(" ", segments);
    }

    public void Dispose()
    {
        _processor?.Dispose();
    }
}
