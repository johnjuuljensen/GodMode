using Windows.ApplicationModel.Calls;
using Windows.Devices.Enumeration;

namespace GodMode.HeadsetSpike;

/// <summary>
/// Windows' VoIP calls (Windows.ApplicationModel.Calls.VoipCallCoordinator), with the call control devices Windows 11
/// 24H2 (10.0.26100) added: a reported VoIP call is associated with the headset, and the headset's call button comes
/// back as <c>EndRequested</c> (and answer, reject, hold, resume, mute). The third trial found that in HFP the button
/// is no media button at all, and Windows.Media.Devices.CallControl is not available here, so this is the remaining
/// way for a press to close the mic. The documentation lists the <c>voipCall</c> capability; whether an unpackaged
/// app may use it is what <see cref="ProbeAsync"/> finds out.
/// </summary>
public sealed class VoipCalls(SpikeLog log)
{
    private const string Source = "VOIP";
    private VoipCallCoordinator? _coordinator;
    private VoipPhoneCall? _call;
    private IReadOnlyList<string> _devices = [];

    public string State { get; private set; } = "none";

    /// <summary>The headset's button asked to end the reported call; on a WinRT thread.</summary>
    public event Action? EndRequested;

    [System.Runtime.Versioning.SupportedOSPlatformGuard("windows10.0.26100.0")]
    private static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100);

    /// <summary>What the platform offers, with no call: the coordinator, the device kinds it associates, the devices.</summary>
    public async Task ProbeAsync()
    {
        if (!Supported)
        {
            log.Write(Source, $"call control devices need Windows 11 24H2 (10.0.26100); this is {Environment.OSVersion.Version}");
            return;
        }
        try
        {
            if (_coordinator is null)
            {
                _coordinator = VoipCallCoordinator.GetDefault();
                log.Write(Source, _coordinator is null ? "VoipCallCoordinator.GetDefault() is null" : "VoipCallCoordinator.GetDefault(): ok");
                if (_coordinator is null) return;
                _coordinator.MuteStateChanged += (_, e) => log.Write(Source, $"MuteStateChanged: muted {e.Muted}");
            }
            foreach (var kind in new[] { VoipCallControlDeviceKind.Bluetooth, VoipCallControlDeviceKind.Usb })
                log.Write(Source, $"{kind} supported for association: {VoipCallCoordinator.IsCallControlDeviceKindSupportedForAssociation(kind)}");
            var devices = await DeviceInformation.FindAllAsync(VoipCallCoordinator.GetDeviceSelectorForCallControl());
            _devices = [.. devices.Select(d => d.Id)];
            log.Write(Source, $"call control devices: {devices.Count}");
            foreach (var device in devices) log.Write(Source, $"  '{device.Name}' {device.Id}");
        }
        catch (Exception ex)
        {
            log.Error(Source, "probe", ex);
        }
    }

    /// <summary>Reports an active outgoing VoIP call, associated with every call control device found.</summary>
    public async Task StartAsync()
    {
        if (_call is not null) return;
        await ProbeAsync();
        if (!Supported || _coordinator is null) return;
        try
        {
            var call = _coordinator.RequestNewOutgoingCall("godmode-headset-spike", "GodMode", "GodMode headset spike", VoipPhoneCallMedia.Audio);
            call.EndRequested += (c, _) =>
            {
                log.Write(Source, "EndRequested (the headset's button?)");
                End("EndRequested");
                EndRequested?.Invoke();
            };
            call.AnswerRequested += (_, e) => log.Write(Source, $"AnswerRequested ({e.AcceptedMedia})");
            call.RejectRequested += (_, e) => log.Write(Source, $"RejectRequested ({e.RejectReason})");
            call.HoldRequested += (_, _) => log.Write(Source, "HoldRequested");
            call.ResumeRequested += (_, _) => log.Write(Source, "ResumeRequested");
            if (_devices.Count > 0) call.NotifyCallActive(_devices);
            else call.NotifyCallActive();
            _call = call;
            State = "active";
            log.Write(Source, $"call active, associated devices: [{string.Join(", ", call.GetAssociatedCallControlDevices())}], using the list: {call.IsUsingAssociatedDevicesList}");
        }
        catch (Exception ex)
        {
            log.Error(Source, "report call", ex);
        }
    }

    /// <summary>Ends the reported call (Windows wants NotifyCallEnded within 5 s of EndRequested).</summary>
    public void End(string why)
    {
        if (_call is not { } call) return;
        _call = null;
        State = "none";
        try
        {
            call.NotifyCallEnded();
            log.Write(Source, $"call ended ({why})");
        }
        catch (Exception ex)
        {
            log.Error(Source, "end call", ex);
        }
    }
}
