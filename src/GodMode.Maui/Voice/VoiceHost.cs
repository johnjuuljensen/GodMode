using GodMode.ClientBase;
using GodMode.ClientBase.Services;
using GodMode.Maui.Bridge;
using GodMode.Voice;
using Microsoft.Extensions.Logging;
using VoiceBot.Core.Pipeline;

namespace GodMode.Maui.Voice;

/// <summary>
/// The app's voice session: it belongs to the app, not the page, so a WebView reload (or a new page) finds it running
/// and gets the conversation so far. Its events go to whichever page is attached now (<see cref="Attach"/>).
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
    private Action<string, object?>? _send;
    private Running? _running;
    private VoiceState _state = VoiceState.Off;
    private VoiceErrorPayload? _error;

    public VoiceHost(IServerDirectory directory, ISecretStore secrets, ILoggerFactory loggerFactory)
    {
        _directory = directory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<VoiceHost>();
        Settings = new VoiceSettingsStore(GodModePaths.AppDataDirectory, secrets);
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
            lock (_lock) return new VoiceStatus(Available, _state, [.. _lines], _error);
        }
    }

    /// <summary>Events go to this page from now on (the one before it is gone or replaced).</summary>
    public void Attach(Action<string, object?> send) => _send = send;

    public async Task<VoiceStatus> StartAsync()
    {
        if (VoiceAudio.Open is not { } open)
            throw new PlatformNotSupportedException("Voice is available in the Windows and Android apps only");

        await _switching.WaitAsync();
        try
        {
            if (_running is not null) return Status;

            var settings = await Settings.LoadAsync();
            var providers = new CloudVoiceProviders(await Settings.LoadKeysAsync());
            if (providers.MissingKeys.Count > 0)
                throw new InvalidOperationException($"Set the {string.Join(" and ", providers.MissingKeys)} key in the voice settings");

            lock (_lock)
            {
                _lines.Clear();
                _error = null;
            }
            StateChanged(VoiceState.Starting);

            IVoiceAudio? audio = null;
            HubServers? servers = null;
            try
            {
                audio = await open(new VoiceAudioRequest(settings.EchoCancellation, AudioLost));
                servers = new HubServers(_directory, _loggerFactory);
                var hub = servers;
                var session = await VoiceSession.StartAsync(new VoiceSessionSetup
                {
                    Settings = settings,
                    Servers = hub,
                    ConnectAsync = ct => hub.ConnectAsync(ConnectWait, ct),
                    Transcription = TranscriptionInput.FromAudio(audio.Source),
                    AudioSink = audio.Sink,
                    Providers = providers,
                    Events = this,
                    LoggerFactory = _loggerFactory,
                    LogDirectory = LogDirectory,
                }, CancellationToken.None);
                audio.Start();

                var running = new Running(session, audio, servers);
                _running = running;
                _ = WatchAsync(running);
                _logger.LogInformation("Voice started ({Language}, echo cancellation {Echo})", settings.Language, settings.EchoCancellation);
                return Status;
            }
            catch
            {
                audio?.Dispose();
                if (servers is not null) await servers.DisposeAsync();
                StateChanged(VoiceState.Off);
                throw;
            }
        }
        finally
        {
            _switching.Release();
        }
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

    /// <summary>The server list changed: connect to new servers and let go of removed ones.</summary>
    public void ServersChanged()
    {
        if (_running is { } running)
            _ = running.Servers.RefreshAsync();
    }

    /// <summary>The network changed: make each connection again.</summary>
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

    // ── IVoiceEvents: kept for a page that comes later, and sent to the one attached ──

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
        _send?.Invoke(ShellMessageTypes.VoiceTranscript, line);
    }

    public void Response(string text)
    {
        var line = new VoiceLine(VoiceSpeaker.Bot, text);
        lock (_lock) Keep(line);
        _send?.Invoke(ShellMessageTypes.VoiceResponse, line);
    }

    public void StateChanged(VoiceState state)
    {
        lock (_lock)
        {
            if (_state == state) return;
            _state = state;
        }
        _send?.Invoke(ShellMessageTypes.VoiceStateChanged, Status);
    }

    public void Error(SessionService service, SessionErrorKind kind, string message)
    {
        var error = new VoiceErrorPayload(service, kind, message);
        lock (_lock) _error = error;
        _send?.Invoke(ShellMessageTypes.VoiceError, error);
    }

    public void Recovered(SessionService service)
    {
        lock (_lock)
        {
            if (_error?.Service == service) _error = null;
        }
        _send?.Invoke(ShellMessageTypes.VoiceRecovered, new VoiceServicePayload(service));
    }

    private void Keep(VoiceLine line)
    {
        _lines.Add(line);
        if (_lines.Count > KeptLines) _lines.RemoveRange(0, _lines.Count - KeptLines);
    }

    private sealed record Running(VoiceSession Session, IVoiceAudio Audio, HubServers Servers) : IAsyncDisposable
    {
        /// <summary>
        /// The audio goes back even when the session's teardown throws (VoiceBot's ElevenLabs engine, disposed twice,
        /// does): on Android it holds the microphone, the foreground service and the audio mode.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Session.DisposeAsync();
            }
            finally
            {
                Audio.Dispose();
                await Servers.DisposeAsync();
            }
        }
    }
}
