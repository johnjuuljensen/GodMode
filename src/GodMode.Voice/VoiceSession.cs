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
    /// it gave up waiting), with how many answered: the handles of what is waiting then are biased for in speech
    /// recognition, and the greeting says when no server answered (<see cref="HubServers.ConnectAsync"/>).
    /// </summary>
    public required Func<CancellationToken, Task<ServersHeard>> ConnectAsync { get; init; }

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
    /// Speech-recognition ghost words to drop: VoiceBot's for the session's languages, but none the user says on
    /// purpose (<see cref="SaidOnPurpose"/>): a dropped "ja" is an answer the session never gets, and a dropped "Hej"
    /// a greeting it never hears.
    /// </summary>
    public static IReadOnlyList<string> NoiseWords(SessionLanguages languages) =>
        [.. StringResources.GetWordList(languages, "noiseWords").Where(w => !SaidOnPurpose.Contains(w))];

    /// <summary>Words that are answers, or say something, never noise, in the session's languages.</summary>
    public static readonly IReadOnlySet<string> AnswerWords =
        new HashSet<string>(["ja", "nej", "jo", "tak", "nej tak", "ja tak", "okay", "ok", "yes", "no", "yeah", "nope"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Greetings in the session's languages: the user says them to the session, as to anyone.</summary>
    public static readonly IReadOnlySet<string> Greetings =
        new HashSet<string>(["hej", "hejsa", "hallo", "goddag", "hey", "hi", "hello"], StringComparer.OrdinalIgnoreCase);

    /// <summary>What is never noise: the answers and the greetings.</summary>
    public static readonly IReadOnlySet<string> SaidOnPurpose =
        new HashSet<string>(AnswerWords.Concat(Greetings), StringComparer.OrdinalIgnoreCase);

    private readonly CancellationTokenSource _stop = new();
    private readonly ServiceProvider _services;
    private readonly AsyncServiceScope _scope;
    private readonly VoiceBotSession _session;
    private readonly VoiceStateTracker _state;
    private readonly ILogger _logger;
    private readonly ElevenLabsSttKeyterms? _keyterms;
    private readonly Lock _keytermsLock = new();
    private HashSet<string>? _keytermsSent;
    private Task _run = Task.CompletedTask;

    private VoiceSession(ServiceProvider services, AsyncServiceScope scope, VoiceBotSession session, VoiceStateTracker state,
        AttentionBoard board, ProjectBoard projects, ProjectHandles handles, ElevenLabsSttKeyterms? keyterms, ILogger logger)
    {
        _keyterms = keyterms;
        _services = services;
        _scope = scope;
        _session = session;
        _state = state;
        Board = board;
        Projects = projects;
        Handles = handles;
        _logger = logger;
    }

    /// <summary>Text the user typed: answered as if they had said it.</summary>
    public ChannelWriter<string> UserText => _session.UserText;

    public VoiceState State => _state.Current;

    /// <summary>The attention lists the session announces from.</summary>
    public AttentionBoard Board { get; }

    /// <summary>Every project the servers have, as the session knows them.</summary>
    public ProjectBoard Projects { get; }

    public ProjectHandles Handles { get; }

    /// <summary>
    /// The terms speech recognition is biased towards now (<see cref="Keyterms"/>); empty when the session's speech
    /// engine takes none.
    /// </summary>
    public IReadOnlyList<string> SttKeyterms => _keyterms?.Current ?? [];

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
        var projects = new ProjectBoard(setup.Servers, handles);
        var board = new AttentionBoard(setup.Servers, handles, projects);
        var conversation = new VoiceConversation();

        var heard = await setup.ConnectAsync(ct);

        var language = new ElevenLabsLanguageOptions
        {
            SttLanguageCode = ElevenLabsLanguageCode.Primary,
            SttSecondaryLanguages = [.. languages.MixedIn.Select(TwoLetter)],
            SttKeyterms = Keyterms(projects.Projects, handles),
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

            var inference = scope.ServiceProvider.GetRequiredService<IInferenceProvider>();
            var tools = new VoiceTools(setup.Servers, board, projects, handles, conversation);
            var session = scope.ServiceProvider.GetRequiredService<SessionFactory>().Build(new SessionInputs(
                new SessionContext(languages),
                GodModeGraph.Build(inference, languages, tools, phrases, heard),
                setup.Transcription,
                setup.AudioSink,
                new EventSink(setup.Events, state, tools.Creates))
            {
                AnnouncementFormatter = new NeverThrowingFormatter(new GodModeAnnouncementFormatter(phrases, conversation), logger),
                Options = new SessionOptions
                {
                    LogDirectory = setup.LogDirectory,
                    NoiseWords = NoiseWords(languages),
                },
            });

            // The session's own terms (its scope's), renewed as projects come and go (VoiceBot#51)
            var voice = new VoiceSession(services, scope, session, state, board, projects, handles,
                scope.ServiceProvider.GetService<ElevenLabsSttKeyterms>(), logger);
            projects.Changed += voice.RefreshKeyterms;
            voice.RefreshKeyterms();
            board.Attach((item, handle) => session.Announcements.TryWrite(new Announcement(phrases.Announce(handle, item.Item), item.Project.Key)));
            tools.Creates.Attach(outcome => session.Announcements.TryWrite(new Announcement(phrases.Created(outcome))));
            state.Release();
            voice._run = voice.RunAsync(languages);
            logger.LogInformation("Voice session started ({Languages}); {Answered} of {Servers} servers answered, {Projects} projects, {Waiting} waiting, {Handles} handles",
                languages, heard.Answered, heard.Servers, projects.Projects.Count, board.Items.Count, handles.All.Count);
            return voice;
        }
        catch
        {
            await scope.DisposeAsync();
            await services.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// What ElevenLabs is biased towards: the command words, then GodMode's names, as many as it takes
    /// (<see cref="ElevenLabsLanguageOptions.MaxRealtimeKeyterms"/> of at most <see cref="ElevenLabsLanguageOptions.MaxRealtimeKeytermLength"/>
    /// characters): the projects' roots ("Assistant"), their profiles ("Outbound"), then their handles ("kappe"), each
    /// in the order of the projects, the one changed last first. A handle that is a number needs none: numbers are
    /// recognized as they are. A name too long to be a keyterm is left out, not cut: a cut name is not what is said.
    /// </summary>
    public static IReadOnlyList<string> Keyterms(IEnumerable<ServerProject> projects, ProjectHandles handles)
    {
        var list = projects.ToList();
        IEnumerable<string?> names = [
            .. list.Select(p => p.Project.RootName),
            .. list.Select(p => p.Project.ProfileName),
            .. list.Select(p => handles.Of(p.Ref)).Where(h => h is null || !h.All(char.IsAsciiDigit)),
        ];
        return [.. GodModeGraph.CommandWords.Concat(names.OfType<string>())
            .Select(t => t.Trim())
            .Where(t => t.Length is > 0 and <= ElevenLabsLanguageOptions.MaxRealtimeKeytermLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(ElevenLabsLanguageOptions.MaxRealtimeKeyterms)];
    }

    /// <summary>
    /// Gives speech recognition the terms of the projects as they are now, and logs them, when they are other terms.
    /// ElevenLabs takes them on a new connection, which it opens at the next pause in speech (VoiceBot#51), so the same
    /// terms in another order change nothing: every status update of a project reorders them.
    /// </summary>
    private void RefreshKeyterms()
    {
        lock (_keytermsLock)
        {
            var terms = Keyterms(Projects.Projects, Handles);
            if (_keytermsSent?.SetEquals(terms) == true) return;
            _keyterms?.Set(terms);
            _keytermsSent = new HashSet<string>(terms, StringComparer.Ordinal);
            _logger.LogInformation("Voice keyterms ({Count}): {Keyterms}", terms.Count, string.Join(" | ", terms));
        }
    }

    public async ValueTask DisposeAsync()
    {
        Projects.Changed -= RefreshKeyterms;
        await _stop.CancelAsync();
        try
        {
            await _run;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice session ended with an error");
        }
        try
        {
            await _session.DisposeAsync();
        }
        catch (Exception ex)
        {
            // VoiceBot logs a failing teardown step rather than throwing it (VoiceBot#64); should one throw even so,
            // the rest is let go of all the same
            _logger.LogWarning(ex, "Voice session's teardown failed");
        }
        _state.Hold(VoiceState.Off);
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
    private sealed class EventSink(IVoiceEvents events, VoiceStateTracker state, SessionCreates creates) : ISessionEventSink
    {
        public Task OnTranscriptionAsync(TranscriptionEvent evt, string? cleanedText)
        {
            events.Transcript(cleanedText ?? evt.Text, evt.IsPartial);
            return Task.CompletedTask;
        }

        public Task OnResponseAsync(string response)
        {
            // Called as the speech starts: a create's read-back arms it, anything else drops the one that waits
            creates.Spoken(response);
            events.Response(response);
            return Task.CompletedTask;
        }

        public Task OnStateChangeAsync(string fromState, string toState) => Task.CompletedTask;

        public Task OnStatusAsync(string message) => Task.CompletedTask;

        public Task OnErrorAsync(SessionError error)
        {
            // A refused key does not come right by itself: the user has to change it. Nor does a microphone that failed
            // (VoiceBot#64: AudioInput, which the app names as the session's own failure; on Windows FollowingAudio
            // keeps the session's source going, so this is Android's)
            if (error.Service == SessionService.AudioInput)
            {
                state.Hold(VoiceState.Error, SessionService.AudioInput);
                events.Error(SessionService.Session, SessionErrorKind.ServiceError, $"The microphone stopped: {error.Message}");
                return Task.CompletedTask;
            }
            if (error.Kind == SessionErrorKind.Authentication) state.Hold(VoiceState.Error, error.Service);
            events.Error(error.Service, error.Kind, error.Message);
            return Task.CompletedTask;
        }

        public Task OnRecoveredAsync(SessionService service)
        {
            state.Release(service);
            events.Recovered(service);
            return Task.CompletedTask;
        }

        public Task OnActivityAsync(SessionActivity activity)
        {
            state.Activity(activity);
            return Task.CompletedTask;
        }
    }
}
