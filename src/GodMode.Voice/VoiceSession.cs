using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VoiceBot.Core.AI;
using VoiceBot.Core.Audio;
using VoiceBot.Core.Pipeline;
using VoiceBot.Core.Resources;
using VoiceBot.Core.Speech;
using VoiceBot.Providers.ElevenLabs;

namespace GodMode.Voice;

/// <summary>What a voice session reports to its host (the app, which passes it on to the page).</summary>
public interface IVoiceEvents
{
    /// <summary>What the user said, as recognized: partial while they speak, then final.</summary>
    void Transcript(string text, bool partial);

    /// <summary>What the bot says.</summary>
    void Response(string text);

    void StateChanged(VoiceState state);

    /// <summary>A service failed; the session keeps running (<see cref="SessionError"/>).</summary>
    void Error(SessionService service, SessionErrorKind kind, string message);

    /// <summary>A service reported through <see cref="Error"/> works again.</summary>
    void Recovered(SessionService service);
}

/// <summary>Everything one voice session is made from.</summary>
public sealed record VoiceSessionSetup
{
    public required VoiceSettings Settings { get; init; }
    public required IGodModeServers Servers { get; init; }

    /// <summary>
    /// Connects to the servers, after the session is listening to them, and returns once what they hold now is in (or
    /// it gave up waiting): the handles of what is waiting then are biased for in speech recognition.
    /// </summary>
    public required Func<CancellationToken, Task> ConnectAsync { get; init; }

    /// <summary>The microphone (<see cref="TranscriptionInput.FromAudio"/>), or transcriptions from elsewhere (a test's).</summary>
    public required TranscriptionInput Transcription { get; init; }

    /// <summary>The speaker.</summary>
    public required IAudioSink AudioSink { get; init; }

    public required IVoiceProviders Providers { get; init; }
    public required IVoiceEvents Events { get; init; }
    public required ILoggerFactory LoggerFactory { get; init; }

    /// <summary>Where the session writes its log (<see cref="SessionOptions.LogDirectory"/>).</summary>
    public required string LogDirectory { get; init; }
}

/// <summary>
/// One voice session over GodMode: VoiceBot's session with GodMode's graph, announcing what starts to need the user on
/// any server, until it is disposed. Build it with <see cref="StartAsync"/>.
/// </summary>
public sealed class VoiceSession : IAsyncDisposable
{
    /// <summary>
    /// Speech-recognition ghost words to drop. None that could be an answer: a dropped "ja" is an answer the session
    /// never gets. VoiceBot's Danish list has "tak", which answers "shall I …?" as well as it thanks.
    /// </summary>
    public static readonly IReadOnlyList<string> NoiseWords = ["hmm", "øh", "ah", "oh", "hej", "hey"];

    /// <summary>Words that are answers, never noise, in the session's languages.</summary>
    public static readonly IReadOnlySet<string> AnswerWords =
        new HashSet<string>(["ja", "nej", "jo", "tak", "nej tak", "ja tak", "okay", "ok", "yes", "no", "yeah", "nope"], StringComparer.OrdinalIgnoreCase);

    private readonly CancellationTokenSource _stop = new();
    private readonly ServiceProvider _services;
    private readonly AsyncServiceScope _scope;
    private readonly VoiceBotSession _session;
    private readonly VoiceStateTracker _state;
    private readonly ILogger _logger;
    private Task _run = Task.CompletedTask;

    private VoiceSession(ServiceProvider services, AsyncServiceScope scope, VoiceBotSession session, VoiceStateTracker state,
        AttentionBoard board, ProjectHandles handles, ILogger logger)
    {
        _services = services;
        _scope = scope;
        _session = session;
        _state = state;
        Board = board;
        Handles = handles;
        _logger = logger;
    }

    /// <summary>Text the user typed: answered as if they had said it.</summary>
    public ChannelWriter<string> UserText => _session.UserText;

    public VoiceState State => _state.Current;

    /// <summary>The attention lists the session announces from.</summary>
    public AttentionBoard Board { get; }

    public ProjectHandles Handles { get; }

    /// <summary>Runs until <see cref="DisposeAsync"/>, or until the session ends by itself.</summary>
    public Task Completion => _run;

    /// <summary>
    /// Connects to the servers, builds the session and starts it.
    /// </summary>
    /// <exception cref="ArgumentException">A setting is invalid (the language).</exception>
    /// <exception cref="InvalidOperationException">A key is missing.</exception>
    public static async Task<VoiceSession> StartAsync(VoiceSessionSetup setup, CancellationToken ct)
    {
        var logger = setup.LoggerFactory.CreateLogger<VoiceSession>();
        var languages = setup.Settings.Languages;
        var phrases = new VoicePhrases(languages);
        var handles = new ProjectHandles();
        var board = new AttentionBoard(setup.Servers, handles);
        var conversation = new VoiceConversation();

        await setup.ConnectAsync(ct);

        var language = new ElevenLabsLanguageOptions
        {
            SttLanguageCode = ElevenLabsLanguageCode.Primary,
            SttSecondaryLanguages = [.. languages.MixedIn.Select(TwoLetter)],
            SttKeyterms = Keyterms(handles.All),
            TtsLanguageCode = ElevenLabsLanguageCode.Primary,
        };

        var collection = new ServiceCollection();
        collection.AddSingleton(setup.LoggerFactory);
        collection.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        collection.AddSingleton<AudioPhraseCache>();
        setup.Providers.Register(collection, setup.Settings, language);
        collection.AddVoiceBotSessions();
        var services = collection.BuildServiceProvider();

        var state = new VoiceStateTracker();
        state.Changed += setup.Events.StateChanged;
        AsyncServiceScope scope = default;
        try
        {
            await setup.Providers.InitializeAsync(services, setup.Settings);
            scope = services.CreateAsyncScope();

            var inference = new ObservedInference(scope.ServiceProvider.GetRequiredService<IInferenceProvider>(), state);
            var tools = new VoiceTools(setup.Servers, board, handles, conversation);
            var session = scope.ServiceProvider.GetRequiredService<SessionFactory>().Build(new SessionInputs(
                new SessionContext(languages),
                GodModeGraph.Build(inference, languages, tools, phrases),
                setup.Transcription,
                new ObservedAudioSink(setup.AudioSink, state),
                new EventSink(setup.Events, state))
            {
                AnnouncementFormatter = new NeverThrowingFormatter(new GodModeAnnouncementFormatter(phrases, conversation), logger),
                Options = new SessionOptions
                {
                    LogDirectory = setup.LogDirectory,
                    NoiseWords = NoiseWords,
                },
            });

            var voice = new VoiceSession(services, scope, session, state, board, handles, logger);
            board.Attach((item, handle) => session.Announcements.TryWrite(new Announcement(phrases.Announce(handle, item.Item), item.Project.Key)));
            state.Release();
            voice._run = voice.RunAsync(languages);
            logger.LogInformation("Voice session started ({Languages}); {Waiting} waiting, {Handles} handles",
                languages, board.Items.Count, handles.All.Count);
            return voice;
        }
        catch
        {
            await state.DisposeAsync();
            await scope.DisposeAsync();
            await services.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// What ElevenLabs is biased towards: the command words, then the handles given so far, as many as it takes
    /// (<see cref="ElevenLabsLanguageOptions.MaxRealtimeKeyterms"/> of at most <see cref="ElevenLabsLanguageOptions.MaxRealtimeKeytermLength"/>
    /// characters). A handle that is a number needs none: numbers are recognized as they are.
    /// </summary>
    public static IReadOnlyList<string> Keyterms(IEnumerable<string> handles) =>
        [.. GodModeGraph.CommandWords.Concat(handles.Where(h => !h.All(char.IsAsciiDigit)))
            .Select(t => t.Trim())
            .Where(t => t.Length is > 0 and <= ElevenLabsLanguageOptions.MaxRealtimeKeytermLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(ElevenLabsLanguageOptions.MaxRealtimeKeyterms)];

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _run;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice session ended with an error");
        }
        await _session.DisposeAsync();
        _state.Hold(VoiceState.Off);
        await _state.DisposeAsync();
        await _scope.DisposeAsync();
        await _services.DisposeAsync();
        _stop.Dispose();
    }

    private async Task RunAsync(SessionLanguages languages)
    {
        await Task.Yield();
        try
        {
            await _session.RunAsync(languages, _stop.Token);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        _logger.LogInformation("Voice session ended");
    }

    private static string TwoLetter(string language) =>
        System.Globalization.CultureInfo.GetCultureInfo(language).TwoLetterISOLanguageName;

    /// <summary>The session's events, to the host and the state.</summary>
    private sealed class EventSink(IVoiceEvents events, VoiceStateTracker state) : ISessionEventSink
    {
        public Task OnTranscriptionAsync(TranscriptionEvent evt, string? cleanedText)
        {
            if (!evt.IsPartial) state.UserSpoke();
            events.Transcript(cleanedText ?? evt.Text, evt.IsPartial);
            return Task.CompletedTask;
        }

        public Task OnResponseAsync(string response)
        {
            events.Response(response);
            return Task.CompletedTask;
        }

        public Task OnStateChangeAsync(string fromState, string toState) => Task.CompletedTask;

        public Task OnStatusAsync(string message) => Task.CompletedTask;

        public Task OnErrorAsync(SessionError error)
        {
            // A refused key does not come right by itself: the user has to change it
            if (error.Kind == SessionErrorKind.Authentication) state.Hold(VoiceState.Error);
            events.Error(error.Service, error.Kind, error.Message);
            return Task.CompletedTask;
        }

        public Task OnRecoveredAsync(SessionService service)
        {
            state.Release();
            events.Recovered(service);
            return Task.CompletedTask;
        }
    }
}
