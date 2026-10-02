using Windows.Media.Devices;

namespace GodMode.HeadsetSpike;

/// <summary>
/// Windows.Media.Devices.CallControl: the app tells Windows a call is ringing or active, and a headset's call button
/// comes back as AnswerRequested or HangUpRequested. The spike's question is whether a press in HFP reaches it, as a
/// way out of call mode by hand, and whether Windows then shows a call. Null-safe: GetDefault is null where no audio
/// device takes call control.
/// </summary>
public sealed class CallReporter(SpikeLog log)
{
    private const string Source = "CALL";
    private CallControl? _control;
    private ulong? _call;

    public string State { get; private set; } = "none";

    /// <summary>Looks for call control; again at each request while there is none (the headset may connect later).</summary>
    public void Start()
    {
        if (_control is not null) return;
        try
        {
            _control = CallControl.GetDefault();
            if (_control is null && MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Communications) is { Length: > 0 } speaker)
            {
                _control = CallControl.FromId(speaker);
                if (_control is not null) log.Write(Source, $"CallControl from the default communications speaker {speaker}");
            }
        }
        catch (Exception ex)
        {
            log.Error(Source, "CallControl.GetDefault", ex);
            return;
        }
        if (_control is null)
        {
            log.Write(Source, "no CallControl, by GetDefault or the default communications speaker: no audio device takes call control");
            State = "unavailable";
            return;
        }
        State = "none";
        log.Write(Source, $"CallControl: HasRinger={_control.HasRinger}");
        _control.AnswerRequested += _ => Event("AnswerRequested");
        _control.HangUpRequested += _ => Event("HangUpRequested");
        _control.DialRequested += (_, e) => Event($"DialRequested ({e.Contact})");
        _control.RedialRequested += (_, _) => Event("RedialRequested");
        _control.KeypadPressed += (_, e) => Event($"KeypadPressed {e.TelephonyKey}");
        _control.AudioTransferRequested += _ => Event("AudioTransferRequested");
    }

    private void Event(string what)
    {
        log.Write(Source, $"{what} (call: {State})");
    }

    /// <summary>A ringing call: the headset's button would answer it.</summary>
    public void Incoming(bool ringer) => Do("IndicateNewIncomingCall", c =>
    {
        _call = c.IndicateNewIncomingCall(ringer, "GodMode");
        State = $"incoming #{_call}";
    });

    /// <summary>An active call, ringing first when there is none: the headset's button would hang it up.</summary>
    public void Active() => Do("IndicateActiveCall", c =>
    {
        _call ??= c.IndicateNewIncomingCall(false, "GodMode");
        c.IndicateActiveCall(_call.Value);
        State = $"active #{_call}";
    });

    public void End() => Do("EndCall", c =>
    {
        if (_call is { } call) c.EndCall(call);
        _call = null;
        State = "none";
    });

    private void Do(string what, Action<CallControl> act)
    {
        Start();
        if (_control is null)
        {
            log.Write(Source, $"{what}: no CallControl");
            return;
        }
        try
        {
            act(_control);
            log.Write(Source, $"{what}: call {State}");
        }
        catch (Exception ex)
        {
            log.Error(Source, what, ex);
        }
    }
}
