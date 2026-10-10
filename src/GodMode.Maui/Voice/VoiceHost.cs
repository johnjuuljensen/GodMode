using System.Collections.Concurrent;
using GodMode.ClientBase;
using GodMode.ClientBase.Services;
using GodMode.Maui.Bridge;
using GodMode.Voice;
using Microsoft.Extensions.Logging;
using VoiceBot.Core.Pipeline;

namespace GodMode.Maui.Voice;

/// <summary>
/// The app's voice session: it belongs to the app, not the page, so a WebView reload (or a new page) finds it running
/// and gets the conversation so far. Its events go to every page attached (<see cref="Attach"/>): one per window, so
/// voice started in one window shows as on in the others (#340). On Windows it owns the mic that opens on demand
/// (<see cref="VoiceMic"/>, issue #422), the music it pauses while it speaks or the mic is open (<see cref="MediaPause"/>),
/// and, while it runs, the headset's play/pause as the mic's switch (issue #423): one media session for the app, however
/// many windows it has.
/// </summary>
public sealed class VoiceHost : IVoiceEvents
{
    /// <summary>How long a start waits for the servers' attention lists, whose handles speech recognition is biased for.</summary>
    private static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(3);
    private const int KeptLines = 60;

    private readonly IServerDirectory _directory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _switching = new(1, 1);
    private readonly Lock _lock = new();
    private readonly List<VoiceLine> _lines = [];
    private readonly ConcurrentDictionary<int, Page> _pages = new();
    private int _attached;
    private Running? _running;
    private VoiceState _state = VoiceState.Off;
    private VoiceErrorPayload? _error;

    public VoiceHost(IServerDirectory directory, ISecretStore secrets, ILoggerFactory loggerFactory)
    {
        _directory = directory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<VoiceHost>();
        Settings = new VoiceSettingsStore(GodModePaths.AppDataDirectory, secrets, loggerFactory.CreateLogger<VoiceSettingsStore>());
    }

    public VoiceSettingsStore Settings { get; }

    /// <summary>Whether this platform has voice (<see cref="VoiceAudio.Open"/>).</summary>
    public static bool Available => VoiceAudio.Open is not null;

    /// <summary>Where the session's logs go: beside the app's own.</summary>
    public static string LogDirectory => Path.Combine(GodModePaths.AppDataDirectory, "logs", "voice");

    public VoiceStatus Status
    {
        get
        {
            var mic = _running?.Mic?.State ?? (VoiceAudio.MicOnDemand ? VoiceMicState.Closed : VoiceMicState.Open);
            lock (_lock) return new VoiceStatus(Available, _state, _lines.ToArray(), _error, mic, VoiceAudio.MicOnDemand);
        }
    }

    /// <summary>
    /// Events go to this page too, from now on, until the result is disposed (its page is gone or replaced). Its window's
    /// <paramref name="profile"/> (null for the main window) is where a project of that profile is shown (<see cref="Show"/>).
    /// </summary>
    public IDisposable Attach(string? profile, Action<string, object?> send)
    {
        var page = Interlocked.Increment(ref _attached);
        _pages[page] = new Page(profile, send);
        return new Detach(() => _pages.TryRemove(page, out _));
    }

    /// <summary>A page attached: the profile its window is locked to (null for the main window), and how to send it an event.</summary>
    private sealed record Page(string? Profile, Action<string, object?> Send);

    private void Send(string type, object? payload)
    {
        foreach (var page in _pages.Values) page.Send(type, payload);
    }

    private sealed class Detach(Action detach) : IDisposable
    {
        public void Dispose() => detach();
    }

    public async Task<VoiceStatus> StartAsync()
    {
        if (VoiceAudio.Open is not { } open)
            throw new PlatformNotSupportedException("Voice is available in the Windows and Android apps only");

        await _switching.WaitAsync();
        try
        {
            if (_running is not null) return Status;

            lock (_lock)
            {
                _lines.Clear();
                _error = null;
            }
            var settings = await Settings.LoadAsync();
            var providers = new CloudVoiceProviders(await Settings.LoadKeysAsync());
            if (providers.MissingKeys.Count > 0)
                throw StartFailed(new InvalidOperationException($"Set the {string.Join(" and ", providers.MissingKeys)} key in the voice settings"));

            StateChanged(VoiceState.Starting);

            IVoiceAudio? audio = null;
            HubServers? servers = null;
            IMediaPlayback? playback = null;
            MediaPause? media = null;
            IHeadsetCall? call = null;
            VoiceMic? mic = null;
            IDisposable? buttons = null;
            try
            {
                audio = await open(new VoiceAudioRequest(settings.EchoCancellation, settings.Microphone, settings.Speaker, AudioLost));
                if (VoiceAudio.Media is { } sessions)
                {
                    playback = sessions();
                    media = new MediaPause(playback, _loggerFactory.CreateLogger<MediaPause>());
                }
                if (audio.Mic is { } micSwitch)
                {
                    call = VoiceAudio.HeadsetCall();
                    mic = new VoiceMic(micSwitch, audio.Sink, media,
                        new VoiceMicOptions { SilenceTimeout = TimeSpan.FromSeconds(settings.MicSilenceSeconds) },
                        _loggerFactory.CreateLogger<VoiceMic>(), call: call);
                    mic.Changed += _ => Send(ShellMessageTypes.VoiceStateChanged, Status);
                }
                servers = new HubServers(_directory, _loggerFactory);
                var hub = servers;
                var session = await VoiceSession.StartAsync(new VoiceSessionSetup
                {
                    Settings = settings,
                    Servers = hub,
                    ConnectAsync = ct => hub.ConnectAsync(ConnectWait, ct),
                    Transcription = TranscriptionInput.FromAudio(audio.Source),
                    AudioSink = audio.Sink,
                    Mic = mic,
                    Media = media,
                    Providers = providers,
                    Events = this,
                    LoggerFactory = _loggerFactory,
                    LogDirectory = LogDirectory,
                }, CancellationToken.None);
                audio.Start();
                // Once it hears: from here the headset's play/pause is the mic's, until voice stops
                if (mic is not null && playback is not null && VoiceAudio.MediaButtons is { } mediaButtons)
                    buttons = mediaButtons(playback, mic.ToggleAsync);

                var running = new Running(session, audio, servers, mic, media, call, buttons);
                _running = running;
                _ = WatchAsync(running);
                _logger.LogInformation("Voice started ({Language}, echo cancellation {Echo}, mic {Mic})", settings.Language, settings.EchoCancellation,
                    mic is null ? "always open" : $"on demand, closing after {settings.MicSilenceSeconds} s of silence");
                return Status;
            }
            catch (Exception ex)
            {
                buttons?.Dispose();
                mic?.Dispose();
                call?.Dispose();
                audio?.Dispose();
                media?.Dispose();
                if (servers is not null) await servers.DisposeAsync();
                StartFailed(ex);
                throw;
            }
        }
        finally
        {
            _switching.Release();
        }
    }

    /// <summary>
    /// A start that failed is Off, with its reason kept in <see cref="Status"/> as a stop's is (<see cref="StopForAsync"/>):
    /// a page loaded after it still shows why. The next start clears it.
    /// </summary>
    private Exception StartFailed(Exception ex)
    {
        _logger.LogWarning("Voice did not start: {Why}", ex.Message);
        Error(SessionService.Session, SessionErrorKind.ServiceError, ex.Message);
        StateChanged(VoiceState.Off);
        return ex;
    }

    public async Task<VoiceStatus> StopAsync()
    {
        await _switching.WaitAsync();
        try
        {
            if (_running is { } running)
            {
                _running = null;
                try
                {
                    await running.DisposeAsync();
                    _logger.LogInformation("Voice stopped");
                }
                catch (Exception ex)
                {
                    // Stopped all the same: the audio and the connections are let go of whatever the session did
                    _logger.LogWarning(ex, "Voice stopped; the session's teardown failed");
                }
            }
            StateChanged(VoiceState.Off);
            return Status;
        }
        finally
        {
            _switching.Release();
        }
    }

    /// <summary>The Mic button (<c>voice.mic.open</c>): pauses the music, opens the microphone, plays the rising tone.</summary>
    public async Task<VoiceStatus> OpenMicAsync()
    {
        await RunningMic().OpenAsync();
        return Status;
    }

    /// <summary>The Mic button (<c>voice.mic.close</c>): the falling tone, then the microphone is let go of.</summary>
    public async Task<VoiceStatus> CloseMicAsync()
    {
        await RunningMic().CloseAsync(MicClose.Button);
        return Status;
    }

    private VoiceMic RunningMic() => _running switch
    {
        null => throw new InvalidOperationException("Voice is off"),
        { Mic: { } mic } => mic,
        _ => throw new PlatformNotSupportedException("The mic is always open here"),
    };

    /// <summary>
    /// Saves the settings (<c>voice.settings.set</c>). A running session moves to the devices they choose now; the rest
    /// applies from its next start.
    /// </summary>
    public async Task<VoiceSettingsView> UpdateSettingsAsync(VoiceSettingsUpdate update)
    {
        var view = await Settings.UpdateAsync(update);
        if ((update.Microphone ?? update.Speaker) is not null && _running is { } running)
            running.Audio.UseDevices(view.Microphone, view.Speaker);
        return view;
    }

    /// <summary>The server list changed: connect to new servers and let go of removed ones.</summary>
    public void ServersChanged() => _ = RefreshServersAsync();

    /// <summary>
    /// Between a start and a stop, never across one: a refresh that overlapped a stop would open connections on the
    /// stopped session's servers, which nothing would close.
    /// </summary>
    private async Task RefreshServersAsync()
    {
        await _switching.WaitAsync();
        try
        {
            if (_running is { } running)
                await running.Servers.RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice: the servers could not be refreshed");
        }
        finally
        {
            _switching.Release();
        }
    }

    /// <summary>
    /// The network changed: make each connection again. Outside <see cref="_switching"/>, as it opens none: it drops
    /// those there are, and a stopped session's servers have none left to drop.
    /// </summary>
    public void NetworkChanged() => _running?.Servers.Reconnect();

    /// <summary>The platform took the audio (a phone call, on Android): stop, and leave the reason on show.</summary>
    private void AudioLost(string why) => _ = StopForAsync(why);

    private async Task StopForAsync(string why)
    {
        _logger.LogWarning("Voice stops: {Why}", why);
        await StopAsync();
        Error(SessionService.Session, SessionErrorKind.ServiceError, why);
    }

    /// <summary>A session that ends by itself (its transcription source ended, say) is stopped as if asked.</summary>
    private async Task WatchAsync(Running running)
    {
        try
        {
            await running.Session.Completion;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Voice session failed");
        }

        if (_running == running)
        {
            _logger.LogWarning("Voice session ended by itself");
            await StopAsync();
        }
    }

    // ── IVoiceEvents: kept for a page that comes later, and sent to every one attached ──

    public void Transcript(string text, bool partial)
    {
        var line = new VoiceLine(VoiceSpeaker.User, text, partial);
        lock (_lock)
        {
            // A partial is replaced by what follows it
            if (_lines is [.., { Speaker: VoiceSpeaker.User, Partial: true }])
                _lines.RemoveAt(_lines.Count - 1);
            Keep(line);
        }
        Send(ShellMessageTypes.VoiceTranscript, line);
    }

    public void Response(string text)
    {
        var line = new VoiceLine(VoiceSpeaker.Bot, text);
        lock (_lock) Keep(line);
        Send(ShellMessageTypes.VoiceResponse, line);
    }

    public void StateChanged(VoiceState state)
    {
        lock (_lock)
        {
            if (_state == state) return;
            _state = state;
        }
        Send(ShellMessageTypes.VoiceStateChanged, Status);
    }

    public void Error(SessionService service, SessionErrorKind kind, string message)
    {
        var error = new VoiceErrorPayload(service, kind, message);
        lock (_lock) _error = error;
        Send(ShellMessageTypes.VoiceError, error);
    }

    public void Recovered(SessionService service)
    {
        lock (_lock)
        {
            if (_error?.Service == service) _error = null;
        }
        Send(ShellMessageTypes.VoiceRecovered, new VoiceServicePayload(service));
    }

    /// <summary>
    /// The user switched to a project by voice (#287): the window of its profile shows it, or the main window when the
    /// profile has none (Android's one window). The window is not brought forward: voice is hands-free, and the user may
    /// be typing in another window, or another app.
    /// </summary>
    public void Show(ProjectRef project, string profile)
    {
        var pages = _pages.Values.ToList();
        var page = pages.FirstOrDefault(p => string.Equals(p.Profile, profile, StringComparison.OrdinalIgnoreCase))
            ?? pages.FirstOrDefault(p => p.Profile is null);
        _logger.LogInformation("Voice shows {Project} ({Profile}) in {Window}", project.ProjectId, profile,
            page is null ? "no window" : page.Profile ?? "the main window");
        page?.Send(ShellMessageTypes.VoiceShow, new VoiceShowPayload(project.ServerId, project.ProjectId));
    }

    private void Keep(VoiceLine line)
    {
        _lines.Add(line);
        if (_lines.Count > KeptLines) _lines.RemoveRange(0, _lines.Count - KeptLines);
    }

    private sealed record Running(VoiceSession Session, IVoiceAudio Audio, HubServers Servers, VoiceMic? Mic, MediaPause? Media,
        IHeadsetCall? Call, IDisposable? Buttons) : IAsyncDisposable
    {
        /// <summary>
        /// The audio and the connections go back even if the session's teardown throws: on Android the audio holds the
        /// microphone, the foreground service and the audio mode. The headset's buttons are the music's again first, and
        /// its call ends. The music it paused resumes last, once the mic is gone.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            Buttons?.Dispose();
            Mic?.Dispose();
            Call?.Dispose();
            try
            {
                await Session.DisposeAsync();
            }
            finally
            {
                try
                {
                    Audio.Dispose();
                    Media?.Dispose();
                }
                finally
                {
                    await Servers.DisposeAsync();
                }
            }
        }
    }
}
