using System.Collections.Concurrent;
using System.Threading.Channels;
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

/// <summary>Text in place of speech recognition: each <see cref="Say"/> is a final transcription.</summary>
internal sealed class TextTranscriptions : ITranscriptionSource
{
    private readonly Channel<TranscriptionEvent> _channel = Channel.CreateUnbounded<TranscriptionEvent>();

    public ChannelReader<TranscriptionEvent> Transcriptions => _channel.Reader;

    public void Say(string text) => _channel.Writer.TryWrite(new TranscriptionEvent { Text = text, IsPartial = false });

    public Task StartAsync(string language, CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A synthesizer that makes a short silence of anything, and keeps what it was asked to say.</summary>
internal sealed class SilentSynthesizer : ISpeechSynthesizer
{
    private static readonly AudioFormat Format = AudioFormat.Pcm16kHz;
    private readonly byte[] _pcm = new byte[Format.BytesPerSecond / 20]; // 50 ms

    public ConcurrentQueue<string> Texts { get; } = new();

    public Task<AudioSegment> SynthesizeAsync(string text, string language, CancellationToken ct)
    {
        Texts.Enqueue(text);
        return Task.FromResult(new AudioSegment { PcmData = _pcm, Format = Format, OriginalText = text });
    }
}

/// <summary>A speaker that plays nothing and counts what it was sent.</summary>
internal sealed class SilentSink : IAudioSink
{
    public AudioFormat Format => AudioFormat.Pcm16kHz;
    public int Chunks;

    public Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref Chunks);
        return Task.CompletedTask;
    }

    public Task SendStatusAsync(string message, CancellationToken ct) => Task.CompletedTask;
    public Task InterruptAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// A model that plays a script: each call takes the next step, which calls a tool or answers with ChatNode's
/// "respond" tool. It keeps every request, so a test can read what the tools gave it. Out of script, it throws.
/// </summary>
internal sealed class ScriptedModel : IInferenceProvider, IChatClient
{
    private readonly ConcurrentQueue<Func<ChatResponse>> _steps = new();
    private int _calls;

    public ConcurrentQueue<IReadOnlyList<ChatMessage>> Requests { get; } = new();

    public ScriptedModel CallTool(string name, Dictionary<string, object?>? arguments = null)
    {
        _steps.Enqueue(() => Reply(new FunctionCallContent($"call-{_calls}", name, arguments ?? [])));
        return this;
    }

    public ScriptedModel Respond(string text)
    {
        _steps.Enqueue(() => Reply(new FunctionCallContent($"call-{_calls}", "respond",
            new Dictionary<string, object?> { ["response_text"] = text })));
        return this;
    }

    /// <summary>The user texts the model was given, in order.</summary>
    public IReadOnlyList<string> UserTexts =>
        [.. Requests.Select(r => r.Last(m => m.Role == ChatRole.User).Text).Distinct()];

    /// <summary>Every tool result the model was given.</summary>
    public IReadOnlyList<string> ToolResults =>
        [.. Requests.SelectMany(r => r).SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Select(c => c.Result?.ToString() ?? "").Distinct()];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        Requests.Enqueue([.. messages]);
        return _steps.TryDequeue(out var step)
            ? Task.FromResult(step())
            : throw new InvalidOperationException("The scripted model ran out of script");
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }

    IChatClient IInferenceProvider.GetClient(InferenceTier tier) => this;

    Task<ChatResponse> IInferenceProvider.CompleteAsync(InferenceTier tier, IList<ChatMessage> messages, ChatOptions? options, CancellationToken ct) =>
        GetResponseAsync(messages, options, ct);

    private static ChatResponse Reply(FunctionCallContent call) => new(new ChatMessage(ChatRole.Assistant, [call]));
}

/// <summary>The session's services without a network: the scripted model and the silent synthesizer.</summary>
internal sealed class OfflineProviders(ScriptedModel model, SilentSynthesizer synthesizer) : IVoiceProviders
{
    public ElevenLabsLanguageOptions? Language { get; private set; }

    public void Register(IServiceCollection services, VoiceSettings settings, ElevenLabsLanguageOptions language)
    {
        Language = language;
        services.AddSingleton<ISpeechSynthesizer>(synthesizer);
        services.AddSingleton<ITranscriptionCleaner, PassthroughCleaner>();
        services.AddSingleton<IInferenceProvider>(model);
    }

    public Task InitializeAsync(IServiceProvider services, VoiceSettings settings) => Task.CompletedTask;
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

    public TextTranscriptions Transcriptions { get; } = new();
    public SilentSynthesizer Synthesizer { get; } = new();
    public RecordingEvents Events { get; } = new();
    public ScriptedModel Model { get; }
    public OfflineProviders Providers { get; }
    public VoiceSession Session { get; private set; } = null!;

    private OfflineVoice(ScriptedModel model)
    {
        Model = model;
        Providers = new OfflineProviders(model, Synthesizer);
    }

    public static async Task<OfflineVoice> StartAsync(IGodModeServers servers, ScriptedModel model,
        Func<CancellationToken, Task>? connect = null, VoiceSettings? settings = null, ILoggerFactory? loggerFactory = null)
    {
        var voice = new OfflineVoice(model);
        voice.Session = await VoiceSession.StartAsync(new VoiceSessionSetup
        {
            Settings = settings ?? VoiceSettings.Default,
            Servers = servers,
            ConnectAsync = connect ?? (_ => Task.CompletedTask),
            Transcription = TranscriptionInput.FromSource(voice.Transcriptions),
            AudioSink = new SilentSink(),
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
