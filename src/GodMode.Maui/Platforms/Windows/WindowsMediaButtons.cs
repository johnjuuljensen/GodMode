using GodMode.Voice;
using Microsoft.Extensions.Logging;
using Windows.Media;
using XamlWindow = Microsoft.UI.Xaml.Window;

namespace GodMode.Maui;

/// <summary>
/// GodMode's own media session while voice is on (issue #423), from the headset spike's <c>OwnMediaControls</c> (#382):
/// the SystemMediaTransportControls of the app's first window (<c>SystemMediaTransportControlsInterop.GetForWindow</c>),
/// one for the app however many windows it has (#338). When that window closes, the session moves to the next one.
/// What it reports and when it reopens is <see cref="HeadsetButtons"/>'; this is Windows' side of it, on the UI thread.
/// </summary>
public sealed class WindowsMediaButtons : IOwnMediaSession
{
    /// <summary>How long the session stays closed when it reopens (the spike's reclaim).</summary>
    private static readonly TimeSpan ReopenGap = TimeSpan.FromMilliseconds(100);

    private readonly ILogger _logger;
    // On the UI thread only
    private SystemMediaTransportControls? _controls;
    private XamlWindow? _window;
    private bool _open;
    private bool _playing;

    private WindowsMediaButtons(ILogger logger) => _logger = logger;

    /// <summary>
    /// The headset's play/pause as <paramref name="playPause"/> until the result is disposed, over the media sessions
    /// voice pauses; null where they are not Windows'.
    /// </summary>
    public static IDisposable? Start(IMediaPlayback playback, Func<Task> playPause, ILogger logger) =>
        playback is IMediaSessions sessions
            ? new HeadsetButtons(new WindowsMediaButtons(logger), sessions, playPause, logger)
            : null;

    public event Action<MediaButton>? Pressed;

    public void Open(bool playing) => OnUiThread(() =>
    {
        _open = true;
        _playing = playing;
        Attach(except: null);
    });

    public void SetPlaying(bool playing) => OnUiThread(() =>
    {
        _playing = playing;
        if (_controls is { IsEnabled: true } controls) controls.PlaybackStatus = Status(playing);
    });

    public Task ReopenAsync() => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        if (_controls is not { } controls) return;
        controls.PlaybackStatus = MediaPlaybackStatus.Closed;
        controls.IsEnabled = false;
        await Task.Delay(ReopenGap);
        if (!_open || _controls != controls) return;
        _playing = true;
        Enable(controls);
    });

    public void Close() => OnUiThread(() =>
    {
        _open = false;
        Detach();
    });

    private static MediaPlaybackStatus Status(bool playing) => playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;

    /// <summary>The session, on the first of the app's windows but <paramref name="except"/> (the one closing).</summary>
    private void Attach(XamlWindow? except)
    {
        var window = Application.Current?.Windows
            .Select(w => w.Handler?.PlatformView as XamlWindow)
            .FirstOrDefault(w => w is not null && w != except);
        if (window is null)
        {
            _logger.LogWarning("Voice: no window for the media session: the headset's button stays the music's");
            return;
        }
        try
        {
            var controls = SystemMediaTransportControlsInterop.GetForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window));
            controls.ButtonPressed += ButtonPressed;
            window.Closed += WindowClosed;
            _controls = controls;
            _window = window;
            Enable(controls);
            _logger.LogInformation("Voice: the media session is on the window \"{Title}\", {Status}", window.Title, controls.PlaybackStatus);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice: Windows gave the window no media session: the headset's button stays the music's");
        }
    }

    private void Enable(SystemMediaTransportControls controls)
    {
        controls.IsEnabled = true;
        // The spike's: Next and Previous come too, and are ignored, rather than going anywhere else
        controls.IsPlayEnabled = controls.IsPauseEnabled = controls.IsStopEnabled = true;
        controls.IsNextEnabled = controls.IsPreviousEnabled = true;
        controls.PlaybackStatus = Status(_playing);
        var display = controls.DisplayUpdater;
        display.Type = MediaPlaybackType.Music;
        display.MusicProperties.Title = "GodMode voice";
        display.MusicProperties.Artist = "Play/pause opens the mic";
        display.Update();
    }

    private void Detach()
    {
        if (_controls is { } controls)
        {
            controls.ButtonPressed -= ButtonPressed;
            try
            {
                controls.PlaybackStatus = MediaPlaybackStatus.Closed;
                controls.IsEnabled = false;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Voice: closing the media session failed");
            }
        }
        if (_window is { } window) window.Closed -= WindowClosed;
        _controls = null;
        _window = null;
    }

    /// <summary>The session's window closed (a profile window, #338): the session moves to the next one.</summary>
    private void WindowClosed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
    {
        var closing = _window;
        Detach();
        if (!_open) return;
        _logger.LogInformation("Voice: the media session's window closed; it moves to the next");
        Attach(except: closing);
    }

    private void ButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args) =>
        Pressed?.Invoke(args.Button switch
        {
            SystemMediaTransportControlsButton.Play => MediaButton.Play,
            SystemMediaTransportControlsButton.Pause => MediaButton.Pause,
            SystemMediaTransportControlsButton.Stop => MediaButton.Stop,
            SystemMediaTransportControlsButton.Next => MediaButton.Next,
            SystemMediaTransportControlsButton.Previous => MediaButton.Previous,
            _ => MediaButton.Other,
        });

    private void OnUiThread(Action action) => MainThread.BeginInvokeOnMainThread(() =>
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice: the media session failed");
        }
    });
}
