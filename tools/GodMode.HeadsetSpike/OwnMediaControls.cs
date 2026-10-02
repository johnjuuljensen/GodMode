using Windows.Media;

namespace GodMode.HeadsetSpike;

/// <summary>
/// The spike's own SystemMediaTransportControls, for its window (SystemMediaTransportControlsInterop.GetForWindow):
/// Windows routes media buttons to the current media session, so this hears them only while it is that session, which
/// it claims by reporting itself playing. The headset's buttons reach Windows only this way (the first trial: no
/// keyboard key and no HID report for any of them), so catching one gesture means being the current session and
/// passing the others on to Spotify (<see cref="Pressed"/>, the proxy in MainForm).
/// </summary>
public sealed class OwnMediaControls(SpikeLog log)
{
    private const string Source = "SMTC";
    private SystemMediaTransportControls? _controls;

    public string State => _controls is null ? "off" : _controls.PlaybackStatus.ToString();

    /// <summary>A button Windows sent to the spike's session; on a WinRT thread.</summary>
    public event Action<SystemMediaTransportControlsButton>? Pressed;

    public void Enable(IntPtr window, MediaPlaybackStatus status)
    {
        try
        {
            if (_controls is null)
            {
                _controls = SystemMediaTransportControlsInterop.GetForWindow(window);
                _controls.ButtonPressed += (_, e) =>
                {
                    log.Write(Source, $"ButtonPressed {e.Button}");
                    Pressed?.Invoke(e.Button);
                };
                _controls.PlaybackPositionChangeRequested += (_, e) => log.Write(Source, $"PlaybackPositionChangeRequested {e.RequestedPlaybackPosition}");
            }
            _controls.IsEnabled = true;
            _controls.IsPlayEnabled = _controls.IsPauseEnabled = _controls.IsStopEnabled = true;
            _controls.IsNextEnabled = _controls.IsPreviousEnabled = true;
            _controls.PlaybackStatus = status;
            var display = _controls.DisplayUpdater;
            display.Type = MediaPlaybackType.Music;
            display.MusicProperties.Title = "GodMode headset spike";
            display.MusicProperties.Artist = "GodMode";
            display.Update();
            log.Write(Source, $"enabled, status {status}");
        }
        catch (Exception ex)
        {
            log.Error(Source, "enable", ex);
        }
    }

    /// <summary>The status the spike reports, while its controls are on: the proxy mirrors Spotify's with it.</summary>
    public void SetStatus(MediaPlaybackStatus status, string why)
    {
        if (_controls is not { IsEnabled: true } controls || controls.PlaybackStatus == status) return;
        controls.PlaybackStatus = status;
        log.Write(Source, $"status -> {status} ({why})");
    }

    public void Disable()
    {
        if (_controls is null) return;
        _controls.PlaybackStatus = MediaPlaybackStatus.Closed;
        _controls.IsEnabled = false;
        log.Write(Source, "disabled");
    }
}
