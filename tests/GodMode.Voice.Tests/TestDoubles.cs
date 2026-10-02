using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceBot.Core.AI;
using VoiceBot.Core.Audio;
using VoiceBot.Core.Pipeline;
using VoiceBot.Core.Speech;
using VoiceBot.Providers.ElevenLabs;

namespace GodMode.Voice.Tests;

/// <summary>Waits for a condition, failing with a description rather than hanging.</summary>
internal static class Eventually
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public static async Task UntilAsync(Func<bool> condition, Func<string> describe, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? Timeout);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Timed out waiting: {describe()}");
            await Task.Delay(25);
        }
    }
}

/// <summary>What GodMode's tests read off VoiceBot.Testing's doubles.</summary>
internal static class TestingExtensions
{
    extension(ListTranscriptionSource transcriptions)
    {
        /// <summary>What ElevenLabs' realtime recognizer sends for an utterance: a partial, then the final with the same text.</summary>
        public void SayAsRecognized(string text)
        {
            transcriptions.AddPartial(text);
            transcriptions.AddFinal(text);
        }
    }

    extension(ScriptedChatClient model)
    {
        /// <summary>The user texts the model was given, in order.</summary>
        public IReadOnlyList<string> UserTexts =>
            [.. model.Requests.Select(r => r.Last(m => m.Role == ChatRole.User).Text).Distinct()];

        /// <summary>Every tool result the model was given.</summary>
        public IReadOnlyList<string> ToolResults =>
            [.. model.Requests.SelectMany(r => r).SelectMany(m => m.Contents).OfType<FunctionResultContent>()
                .Select(c => c.Result?.ToString() ?? "").Distinct()];
    }
}

/// <summary>The session's services without a network: the scripted model and a synthesizer of silence.</summary>
internal sealed class OfflineProviders(IInferenceProvider model, FixedPcmSynthesizer synthesizer) : IVoiceProviders
{
    public ElevenLabsLanguageOptions? Language { get; private set; }

    public void Register(IServiceCollection services, VoiceSettings settings, ElevenLabsLanguageOptions language)
    {
        Language = language;
        services.AddSingleton<ISpeechSynthesizer>(synthesizer);
        services.AddSingleton<ITranscriptionCleaner, PassthroughCleaner>();
        services.AddSingleton<IInferenceProvider>(model);
        // The session's keyterms, as AddVoiceBotElevenLabs registers them
        services.AddScoped(_ => new ElevenLabsSttKeyterms(language.SttKeyterms));
        // Heard only by a session fed from a microphone (TranscriptionInput.FromAudio)
        services.AddTransient<ISpeechEngine, ScriptedSpeechEngine>();
    }

    public Task InitializeAsync(IServiceProvider services, VoiceSettings settings) => Task.CompletedTask;
}

/// <summary>The scripted model, behind the tier each request asks for.</summary>
internal sealed class TieredModel(ScriptedChatClient model) : IInferenceProvider
{
    /// <summary>The tier of each request, in order.</summary>
    public ConcurrentQueue<InferenceTier> Tiers { get; } = new();

    public IChatClient GetClient(InferenceTier tier)
    {
        Tiers.Enqueue(tier);
        return model;
    }

    public Task<ChatResponse> CompleteAsync(InferenceTier tier, IList<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        Tiers.Enqueue(tier);
        return model.GetResponseAsync(messages, options, ct);
    }
}

/// <summary>What a session reported, in order.</summary>
internal sealed class RecordingEvents : IVoiceEvents
{
    public ConcurrentQueue<string> Transcripts { get; } = new();
    public ConcurrentQueue<string> Responses { get; } = new();
    public ConcurrentQueue<VoiceState> States { get; } = new();
    public ConcurrentQueue<(SessionService Service, SessionErrorKind Kind, string Message)> Errors { get; } = new();

    public void Transcript(string text, bool partial) { if (!partial) Transcripts.Enqueue(text); }
    public void Response(string text) => Responses.Enqueue(text);
    public void StateChanged(VoiceState state) => States.Enqueue(state);
    public void Error(SessionService service, SessionErrorKind kind, string message) => Errors.Enqueue((service, kind, message));
    public void Recovered(SessionService service) { }

    public Task SaidAsync(string text) =>
        Eventually.UntilAsync(() => Responses.Contains(text), () => $"the bot to say \"{text}\"; it said: {string.Join(" | ", Responses)}");
}

/// <summary>A running voice session over offline services, fed text, and the setup it was started with.</summary>
internal sealed class OfflineVoice : IAsyncDisposable
{
    private readonly string _logDirectory = Path.Combine(Path.GetTempPath(), $"godmode-voice-{Guid.NewGuid():N}");

    public ListTranscriptionSource Transcriptions { get; } = new();
    public FixedPcmSynthesizer Synthesizer { get; }
    public RecordingEvents Events { get; } = new();
    public ScriptedChatClient Model { get; }
    public OfflineProviders Providers { get; }
    public VoiceSession Session { get; private set; } = null!;

    private OfflineVoice(ScriptedChatClient model, IInferenceProvider inference, TimeSpan speech)
    {
        Synthesizer = new FixedPcmSynthesizer(speech);
        Model = model;
        Providers = new OfflineProviders(inference, Synthesizer);
    }

    public static async Task<OfflineVoice> StartAsync(IGodModeServers servers, ScriptedChatClient model,
        Func<CancellationToken, Task>? connect = null, VoiceSettings? settings = null, ILoggerFactory? loggerFactory = null,
        TimeSpan? speech = null, IAudioSource? microphone = null, IInferenceProvider? inference = null)
    {
        // How long anything the bot says plays: short, unless a test watches it speak
        var voice = new OfflineVoice(model, inference ?? model, speech ?? TimeSpan.FromMilliseconds(50));
        voice.Session = await VoiceSession.StartAsync(new VoiceSessionSetup
        {
            Settings = settings ?? VoiceSettings.Default,
            Servers = servers,
            // A fake is connected to as a hub is: its projects first
            ConnectAsync = async ct =>
            {
                if (servers is FakeServers fake) await fake.ConnectAsync(ct);
                if (connect is not null) await connect(ct);
            },
            Transcription = microphone is null ? TranscriptionInput.FromSource(voice.Transcriptions) : TranscriptionInput.FromAudio(microphone),
            AudioSink = new RecordingAudioSink(),
            Providers = voice.Providers,
            Events = voice.Events,
            LoggerFactory = loggerFactory ?? NullLoggerFactory.Instance,
            LogDirectory = voice._logDirectory,
        }, CancellationToken.None);
        return voice;
    }

    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync();
        try { Directory.Delete(_logDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
