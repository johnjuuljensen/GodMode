using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using Windows.Media;
using VoiceBot.Providers.Windows;

namespace GodMode.HeadsetSpike;

/// <summary>
/// The spike's one window: the state at the top (mic, the headset's profile, what plays, the call, the last gesture),
/// the controls the README's trial script presses, and the log below. Everything it does and hears goes to the log.
/// </summary>
public sealed class MainForm : Form
{
    private const int WM_HOTKEY = 0x0312, WM_APPCOMMAND = 0x0319;
    private const int MarkHotkey = 1;

    private readonly SpikeLog _log = new();
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly Endpoints _endpoints;
    private readonly MediaSessions _sessions;
    private readonly CallReporter _call;
    private readonly OwnMediaControls _smtc;
    private readonly Mic _mic;
    private readonly Tone _tone;
    private readonly LeAudioProbe _leAudio;
    private KeyboardHook? _hook;
    private GestureClassifier _hookGestures = new();
    private GestureClassifier _rawGestures = new();
    private int _lastRawUsage;
    private string _lastGesture = "(none)";
    private int _ticks;

    private readonly TextBox _logView = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 8.5f) };
    private readonly Label _state = new() { AutoSize = true, Font = new Font(FontFamily.GenericMonospace, 9f), Padding = new Padding(4) };
    private readonly TextBox _headsetName = new() { Text = "OpenRun", Width = 120 };
    private readonly ComboBox _microphones = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly ComboBox _speakers = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly ComboBox _swallow = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly NumericUpDown _toneOffset = new() { Maximum = 10000, Increment = 100, Value = 0, Width = 70 };
    private readonly NumericUpDown _toneLength = new() { Minimum = 20, Maximum = 2000, Increment = 10, Value = (decimal)Tone.DefaultLength.TotalMilliseconds, Width = 60 };
    private readonly NumericUpDown _toneVolume = new() { Minimum = 1, Maximum = 100, Value = 15, Width = 50 };
    private readonly CheckBox _toneWaitsForSound = new() { Text = "tone waits for first mic sound", AutoSize = true };
    private readonly NumericUpDown _gap = new() { Minimum = 50, Maximum = 3000, Increment = 50, Value = (decimal)GestureClassifier.DefaultGap.TotalMilliseconds, Width = 60 };
    private readonly NumericUpDown _longPress = new() { Minimum = 100, Maximum = 5000, Increment = 100, Value = (decimal)GestureClassifier.DefaultLongPress.TotalMilliseconds, Width = 60 };
    private readonly ComboBox _caught = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly CheckBox _proxy = new() { Text = "proxy: catch it, forward the rest", AutoSize = true };
    private readonly CheckBox _mirror = new() { Text = "mirror Spotify's play/pause", AutoSize = true, Checked = true };
    private readonly TextBox _note = new() { Width = 300, PlaceholderText = "a note for the log (what you pressed, what you heard)" };

    private sealed record Choice<T>(string Label, T Value)
    {
        public override string ToString() => Label;
    }

    public MainForm()
    {
        Text = "GodMode headset spike (#382)";
        Width = 1250;
        Height = 900;
        _endpoints = new Endpoints(_log);
        _sessions = new MediaSessions(_log);
        _call = new CallReporter(_log);
        _smtc = new OwnMediaControls(_log);
        _mic = new Mic(_log);
        _tone = new Tone(_log);
        _leAudio = new LeAudioProbe(_log);
        _log.Written += _pending.Enqueue;
        Application.ThreadException += (_, e) => _log.Write("ERROR", $"UI thread: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => _log.Write("ERROR", $"unhandled: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => _log.Write("ERROR", $"task: {e.Exception}");

        var controls = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        controls.Controls.Add(_state);
        controls.Controls.Add(Row("Headset", new Label { Text = "name has", AutoSize = true }, _headsetName,
            Button("Rescan endpoints", () => { _endpoints.HeadsetName = _headsetName.Text; _ = Task.Run(_endpoints.Survey); FillDevices(); }),
            Button("Probe LE Audio", () => _ = _leAudio.RunAsync(_headsetName.Text))));
        controls.Controls.Add(Row("Mic", _microphones,
            Button("Open mic", () => _ = OpenMicAsync()), Button("Close mic", () => _ = CloseMicAsync()),
            Button("Open mic + rising tone", () => _ = OpenWithToneAsync()), Button("Close mic + falling tone", () => _ = CloseWithToneAsync()),
            new Label { Text = "tone offset ms", AutoSize = true }, _toneOffset, _toneWaitsForSound));
        controls.Controls.Add(Row("Tone", _speakers, new Label { Text = "ms", AutoSize = true }, _toneLength, new Label { Text = "vol %", AutoSize = true }, _toneVolume,
            Button("Play rising", () => _ = PlayToneAsync(rising: true)), Button("Play falling", () => _ = PlayToneAsync(rising: false))));
        controls.Controls.Add(Row("Media", Button("Pause current", () => _ = _sessions.PauseCurrentAsync()),
            Button("Play current", () => _ = _sessions.PlayCurrentAsync()), Button("Toggle current", () => _ = _sessions.ToggleCurrentAsync()),
            Button("Pause all playing", () => _ = _sessions.PausePlayingAsync()), Button("Resume paused", () => _ = _sessions.ResumePausedAsync()),
            Button("Announcement test", () => _ = AnnouncementAsync())));
        controls.Controls.Add(Row("Keys", new Label { Text = "swallow", AutoSize = true }, _swallow,
            Button("Claim SMTC (playing)", () => _smtc.Enable(Handle, MediaPlaybackStatus.Playing)),
            Button("Claim SMTC (paused)", () => _smtc.Enable(Handle, MediaPlaybackStatus.Paused)),
            Button("Release SMTC", _smtc.Disable),
            new Label { Text = "gesture gap ms", AutoSize = true }, _gap, new Label { Text = "long ms", AutoSize = true }, _longPress));
        controls.Controls.Add(Row("Proxy", _proxy, new Label { Text = "catch", AutoSize = true }, _caught,
            _mirror, Button("Start proxy", StartProxy), Button("Stop proxy", StopProxy)));
        controls.Controls.Add(Row("Call", Button("Report incoming (ringing)", () => _call.Incoming(ringer: true)),
            Button("Report incoming (silent)", () => _call.Incoming(ringer: false)),
            Button("Report active call", _call.Active), Button("End call", _call.End)));
        controls.Controls.Add(Row("Log", Button("Mark (Ctrl+Alt+M)", () => Mark("button")), _note,
            Button("Add note", () => { _log.Write("NOTE", _note.Text); _note.Clear(); }),
            Button("Open log folder", () => Process.Start("explorer.exe", SpikeLog.Folder)),
            new Label { Text = _log.Path, AutoSize = true }));

        Controls.Add(_logView);
        Controls.Add(controls);

        _swallow.Items.Add(new Choice<int?>("(none)", null));
        foreach (var vk in MediaKey.All) _swallow.Items.Add(new Choice<int?>(MediaKey.Name(vk), vk));
        _swallow.SelectedIndex = 0;
        _swallow.SelectedIndexChanged += (_, _) =>
        {
            var vk = ((Choice<int?>)_swallow.SelectedItem!).Value;
            if (_hook is not null) _hook.Swallow = vk;
            _log.Write("KEY", $"swallow: {(vk is { } v ? MediaKey.Name(v) : "none")}");
        };
        foreach (var button in new[] { SystemMediaTransportControlsButton.Previous, SystemMediaTransportControlsButton.Next })
            _caught.Items.Add(new Choice<SystemMediaTransportControlsButton>(button.ToString(), button));
        _caught.SelectedIndex = 0;
        _smtc.Pressed += OnOwnButton;
        _sessions.StateChanged += (id, status) =>
        {
            if (MediaSessions.IsOwn(id)) return;
            BeginInvoke(() =>
            {
                if (_proxy.Checked && _mirror.Checked)
                    _smtc.SetStatus(status == Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                        ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused, $"mirrors {id}");
            });
        };
        _gap.ValueChanged += (_, _) => NewClassifiers();
        _longPress.ValueChanged += (_, _) => NewClassifiers();
        _mic.FirstSound += () => _log.Write("MIC", "(first sound: a tone from here on is not cut by the switch, if the switch is done)");

        var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    private static FlowLayoutPanel Row(string title, params Control[] items)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(2) };
        row.Controls.Add(new Label { Text = title, Width = 60, Font = new Font(DefaultFont, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft });
        foreach (var item in items)
        {
            item.Anchor = AnchorStyles.Left;
            row.Controls.Add(item);
        }
        return row;
    }

    private static Button Button(string text, Action click)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += (_, _) => click();
        return button;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _log.Write("APP", $"GodMode headset spike, Windows {Environment.OSVersion.Version}, {RuntimeInformation.OSArchitecture}");
        _log.Write("APP", $"log: {_log.Path}");
        _log.Write("APP", $"mic: WaveIn {Mic.Format.SampleRate} Hz {Mic.Format.Channels} ch, 100 ms buffers; gesture gap {_gap.Value} ms, long press {_longPress.Value} ms");
        try
        {
            _hook = new KeyboardHook(OnHookKey);
            _log.Write("KEY", "low-level keyboard hook installed");
        }
        catch (Exception ex)
        {
            _log.Error("KEY", "keyboard hook", ex);
        }
        try
        {
            RawConsumerInput.Register(Handle);
            _log.Write("RAW", "raw input registered for HID consumer control (0x0C/0x01), in the background too");
        }
        catch (Exception ex)
        {
            _log.Error("RAW", "raw input", ex);
        }
        if (!RegisterHotKey(Handle, MarkHotkey, 0x0001 | 0x0002 | 0x4000, 'M')) // Alt, Control, no repeat
            _log.Write("APP", "Ctrl+Alt+M is taken: mark with the button");
        _ = Task.Run(_endpoints.Survey);
        FillDevices();
        _call.Start();
        _ = _sessions.StartAsync();
    }

    private void FillDevices()
    {
        _microphones.Items.Clear();
        var defaultMic = WindowsAudioDevices.DefaultMicrophoneId(AudioDeviceRole.Communications);
        _microphones.Items.Add(new Choice<string?>("Default communications mic", defaultMic));
        foreach (var mic in WindowsAudioDevices.Microphones()) _microphones.Items.Add(new Choice<string?>(mic.Name, mic.Id));
        _microphones.SelectedIndex = 0;

        _speakers.Items.Clear();
        _speakers.Items.Add(new Choice<(string?, Role)>("Default speaker", (null, Role.Multimedia)));
        _speakers.Items.Add(new Choice<(string?, Role)>("Default communications speaker", (null, Role.Communications)));
        foreach (var speaker in WindowsAudioDevices.Speakers()) _speakers.Items.Add(new Choice<(string?, Role)>(speaker.Name, (speaker.Id, Role.Multimedia)));
        _speakers.SelectedIndex = 0;
    }

    private void NewClassifiers()
    {
        _hookGestures = new GestureClassifier(TimeSpan.FromMilliseconds((double)_gap.Value), TimeSpan.FromMilliseconds((double)_longPress.Value));
        _rawGestures = new GestureClassifier(_hookGestures.Gap, _hookGestures.LongPress);
        _log.Write("KEY", $"gesture gap {_gap.Value} ms, long press {_longPress.Value} ms");
    }

    private void OnHookKey(int vk, bool down, bool injected, bool swallowed)
    {
        _log.Write("HOOK", $"{MediaKey.Name(vk)} {(down ? "down" : "up")}{(injected ? " (injected)" : "")}{(swallowed ? " SWALLOWED" : "")}");
        Gestures("HOOK", _hookGestures.Feed(new KeyEdge(vk, down, _log.Now)), MediaKey.Name);
    }

    private void Gestures(string source, IEnumerable<Gesture> gestures, Func<int, string> name)
    {
        foreach (var gesture in gestures)
        {
            _lastGesture = $"{source} {gesture.Name} {name(gesture.Key)}";
            _log.Write("GESTUR", $"{_lastGesture} ({(gesture.End - gesture.Start).TotalMilliseconds:F0} ms)");
        }
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case RawConsumerInput.WM_INPUT:
                if (RawConsumerInput.Read(m.LParam) is var (device, report))
                {
                    _log.Write("RAW", $"{RawConsumerInput.Describe(report)} from {device}");
                    var usage = report.Length >= 3 ? report[1] | (report[2] << 8) : 0;
                    var key = usage != 0 ? usage : _lastRawUsage;
                    if (usage != 0) _lastRawUsage = usage;
                    if (key != 0) Gestures("RAW", _rawGestures.Feed(new KeyEdge(key, usage != 0, _log.Now)), u => $"usage 0x{u:X}");
                }
                break;
            case WM_APPCOMMAND:
                _log.Write("APPCMD", $"WM_APPCOMMAND {((int)((long)m.LParam >> 16) & 0x0FFF)} (window focused)");
                break;
            case WM_HOTKEY when m.WParam == MarkHotkey:
                Mark("Ctrl+Alt+M");
                break;
        }
        base.WndProc(ref m);
    }

    /// <summary>
    /// The proxy: the spike holds the current media session (it reports itself playing, so Windows sends it the
    /// headset's buttons), catches one button as GodMode's "step in", and passes every other one on to Spotify.
    /// With mirroring it then reports Spotify's play/pause as its own: the second trial found that a proxy reporting
    /// "playing" while Spotify was paused got no button at all, while one reporting "paused" got Play in the first.
    /// </summary>
    private void StartProxy()
    {
        _proxy.Checked = true;
        _smtc.Enable(Handle, MediaPlaybackStatus.Playing);
        _log.Write("PROXY", $"started: catches {_caught.SelectedItem}, forwards the rest, {(_mirror.Checked ? "mirrors Spotify's play/pause" : "always playing")}");
        if (_mirror.Checked && _sessions.OtherPlaying() is false)
            _smtc.SetStatus(MediaPlaybackStatus.Paused, "mirrors the other session at start");
    }

    private void StopProxy()
    {
        _proxy.Checked = false;
        _smtc.Disable();
        _log.Write("PROXY", "stopped");
    }

    // On a WinRT thread: reads the controls' state through Invoke
    private void OnOwnButton(SystemMediaTransportControlsButton button)
    {
        var (proxy, caught) = ((bool, SystemMediaTransportControlsButton))Invoke(() =>
            (_proxy.Checked, ((Choice<SystemMediaTransportControlsButton>)_caught.SelectedItem!).Value));
        if (!proxy) return;
        if (button == caught)
        {
            _log.Write("PROXY", $"CAUGHT {button}: GodMode would step in here (rising tone)");
            _ = PlayToneAsync(rising: true);
        }
        else
            _ = _sessions.ForwardAsync(button);
    }

    private void Mark(string how) => _log.Write("MARK", $"mark ({how})");

    private string? MicrophoneId => ((Choice<string?>?)_microphones.SelectedItem)?.Value;

    /// <summary>Off the UI thread (<see cref="Mic.OpenAsync"/>), which keeps running the hook and Mark while the headset switches.</summary>
    private async Task OpenMicAsync()
    {
        await _mic.OpenAsync(MicrophoneId);
        _endpoints.OpenMicrophoneId = _mic.EndpointId;
    }

    private async Task CloseMicAsync()
    {
        await _mic.CloseAsync();
        _endpoints.OpenMicrophoneId = null;
    }

    private Task PlayToneAsync(bool rising)
    {
        var (id, role) = ((Choice<(string?, Role)>)_speakers.SelectedItem!).Value;
        return _tone.PlayAsync(rising, TimeSpan.FromMilliseconds((double)_toneLength.Value), (float)_toneVolume.Value / 100f, id, role);
    }

    /// <summary>Step 3 by hand: the mic opens, then the rising tone, after the offset (or after the first mic sound and the offset).</summary>
    private async Task OpenWithToneAsync()
    {
        var sound = new TaskCompletionSource();
        void Heard() => sound.TrySetResult();
        _mic.FirstSound += Heard;
        try
        {
            await OpenMicAsync();
            if (_toneWaitsForSound.Checked && await Task.WhenAny(sound.Task, Task.Delay(TimeSpan.FromSeconds(10))) != sound.Task)
                _log.Write("TONE", "no mic sound in 10 s: the tone plays anyway");
            await Task.Delay((int)_toneOffset.Value);
            await PlayToneAsync(rising: true);
        }
        finally
        {
            _mic.FirstSound -= Heard;
        }
    }

    /// <summary>Step 4 by hand: the mic closes, then the falling tone after the offset.</summary>
    private async Task CloseWithToneAsync()
    {
        await CloseMicAsync();
        await Task.Delay((int)_toneOffset.Value);
        await PlayToneAsync(rising: false);
    }

    /// <summary>Step 2 by hand: pause what plays, a stand-in for speech (three tones over A2DP), resume.</summary>
    private async Task AnnouncementAsync()
    {
        _log.Reference("announcement");
        var paused = await _sessions.PausePlayingAsync();
        if (paused > 0)
            _log.Write("GSMTC", await _sessions.WaitNonePlayingAsync(TimeSpan.FromSeconds(3)) ? "nothing plays now" : "still playing after 3 s");
        for (var i = 0; i < 3; i++) await PlayToneAsync(rising: i % 2 == 0);
        await _sessions.ResumePausedAsync();
    }

    private void Tick()
    {
        _endpoints.PollMetersInBackground();
        if (++_ticks % 10 == 0) Task.Run(_endpoints.PollFormats);
        var now = _log.Now;
        if (_hookGestures.Flush(now) is { } hook) Gestures("HOOK", [hook], MediaKey.Name);
        if (_rawGestures.Flush(now) is { } raw) Gestures("RAW", [raw], u => $"usage 0x{u:X}");

        if (!_pending.IsEmpty)
        {
            var lines = new List<string>();
            while (_pending.TryDequeue(out var line)) lines.Add(line);
            if (_logView.TextLength > 400_000) _logView.Text = _logView.Text[^200_000..];
            _logView.AppendText(string.Join(Environment.NewLine, lines) + Environment.NewLine);
        }

        var (reference, since) = _log.SinceReference;
        _state.Text = string.Join(Environment.NewLine,
            $"Mic:      {(_mic.IsOpen ? $"OPEN, level {_mic.Level:P0}" : "closed")}",
            $"Profile:  {_endpoints.Profile}",
            $"Playing:  {_sessions.Playing}",
            $"Call:     {_call.State}    SMTC: {_smtc.State}    swallow: {_swallow.SelectedItem}",
            $"Gesture:  {_lastGesture}",
            $"Since:    {reference} {since.TotalSeconds:F1} s",
            _endpoints.Detail);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        UnregisterHotKey(Handle, MarkHotkey);
        _hook?.Dispose();
        _mic.CloseAsync().Wait(TimeSpan.FromSeconds(5)); // Mic awaits without the UI context, so this cannot deadlock
        if (_call.State.StartsWith("incoming") || _call.State.StartsWith("active")) _call.End();
        _smtc.Disable();
        _endpoints.Dispose();
        _log.Write("APP", "closed");
        _log.Dispose();
        base.OnFormClosed(e);
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
