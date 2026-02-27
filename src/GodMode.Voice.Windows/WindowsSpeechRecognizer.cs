using GodMode.Voice.Services;
using GodMode.Voice.Speech;
using Windows.Globalization;
using Windows.Media.SpeechRecognition;

namespace GodMode.Voice.Windows;

public sealed class WindowsSpeechRecognizer : ISpeechRecognizer
{
    private readonly InferenceConfig _config;

    public WindowsSpeechRecognizer()
    {
        _config = InferenceConfig.Load();
    }

    public string EngineName => "Windows Speech Recognition (offline)";

    public event EventHandler<string>? PartialResultReceived;

    public Task<bool> IsAvailableAsync()
    {
        try
        {
            var languages = SpeechRecognizer.SupportedGrammarLanguages;
            return Task.FromResult(languages.Count > 0);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Returns BCP-47 tags of installed offline speech recognition languages.
    /// </summary>
    public static IReadOnlyList<string> GetInstalledLanguages()
    {
        return SpeechRecognizer.SupportedGrammarLanguages
            .Select(l => l.LanguageTag)
            .ToList();
    }

    public async Task<string> RecognizeSpeechAsync(CancellationToken ct = default)
    {
        var language = ResolveLanguage();
        using var recognizer = new SpeechRecognizer(language);

        // No TopicConstraint — those require online/privacy policy.
        // With no constraints, the recognizer uses the installed offline grammar.
        var compilation = await recognizer.CompileConstraintsAsync();

        if (compilation.Status != SpeechRecognitionResultStatus.Success)
        {
            var installed = GetInstalledLanguages();
            throw new InvalidOperationException(
                $"Speech recognition init failed: {compilation.Status}\n" +
                $"Requested language: {language.LanguageTag}\n" +
                $"Installed languages: {string.Join(", ", installed)}\n\n" +
                "Install speech packs via:\n" +
                "  Settings > Time & Language > Language & Region > [Language] > Options > Speech");
        }

        recognizer.HypothesisGenerated += (s, e) =>
        {
            PartialResultReceived?.Invoke(this, e.Hypothesis.Text);
        };

        var result = await recognizer.RecognizeAsync().AsTask(ct);

        return result.Status switch
        {
            SpeechRecognitionResultStatus.Success => result.Text,
            SpeechRecognitionResultStatus.UserCanceled => string.Empty,
            SpeechRecognitionResultStatus.MicrophoneUnavailable =>
                throw new InvalidOperationException("No microphone detected."),
            _ => throw new InvalidOperationException(
                $"Speech recognition failed: {result.Status}")
        };
    }

    private Language ResolveLanguage()
    {
        var tag = _config.SpeechLanguage;

        // Check if the requested language is installed
        var installed = SpeechRecognizer.SupportedGrammarLanguages;
        var match = installed.FirstOrDefault(l =>
            l.LanguageTag.Equals(tag, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
            return match;

        // Try matching just the primary language (e.g., "en" matches "en-US")
        var primary = tag.Split('-')[0];
        match = installed.FirstOrDefault(l =>
            l.LanguageTag.StartsWith(primary, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
            return match;

        // Fall back to system default
        return SpeechRecognizer.SystemSpeechLanguage;
    }
}
