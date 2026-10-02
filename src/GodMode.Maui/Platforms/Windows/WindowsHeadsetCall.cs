using GodMode.Voice;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel.Calls;
using Windows.Devices.Enumeration;

namespace GodMode.Maui;

/// <summary>
/// The VoIP calls held while voice's mic is open (issue #423), from the headset spike's <c>VoipCalls</c> (#382): its
/// call control device is the headset (Windows 11 24H2's <c>VoipCallCoordinator</c> call control devices,
/// <c>NotifyCallActive(deviceIds)</c>), so the headset's button in HFP, which is no media button there, comes back as the
/// call's <c>EndRequested</c>. Windows shows a call in the tray meanwhile. The unpackaged app needs no capability for it
/// (the spike's fourth trial). The app's minimum, Windows 11 24H2 (10.0.26100), has it. One call at a time is
/// <see cref="HeadsetCall"/>'s.
/// </summary>
public sealed class WindowsHeadsetCall : IPhoneLine
{
    private readonly ILogger _logger;
    private VoipCallCoordinator? _coordinator;

    private WindowsHeadsetCall(ILogger logger) => _logger = logger;

    /// <summary>The headset's call.</summary>
    public static IHeadsetCall Create(ILogger logger) => new HeadsetCall(new WindowsHeadsetCall(logger), logger);

    public async Task<IPhoneCall?> RequestAsync()
    {
        var coordinator = _coordinator ??= VoipCallCoordinator.GetDefault();
        if (coordinator is null)
        {
            _logger.LogWarning("Voice: Windows has no VoIP call coordinator: the headset's button cannot close the mic");
            return null;
        }
        // Afresh at each start: the headset may have connected since
        var devices = await DeviceInformation.FindAllAsync(VoipCallCoordinator.GetDeviceSelectorForCallControl());
        var ids = devices.Select(d => d.Id).ToList();
        if (ids.Count == 0)
        {
            _logger.LogInformation("Voice: no call control device: the headset's button cannot close the mic");
            return null;
        }
        _logger.LogInformation("Voice: the headset's call, with {Devices}", string.Join(", ", devices.Select(d => d.Name)));
        return new Call(coordinator.RequestNewOutgoingCall("godmode-voice", "GodMode", "GodMode voice", VoipPhoneCallMedia.Audio), ids);
    }

    private sealed class Call : IPhoneCall
    {
        private readonly VoipPhoneCall _call;
        private readonly IReadOnlyList<string> _devices;

        public Call(VoipPhoneCall call, IReadOnlyList<string> devices)
        {
            _call = call;
            _devices = devices;
            _call.EndRequested += (_, _) => EndRequested?.Invoke();
        }

        public event Action? EndRequested;

        public void NotifyActive() => _call.NotifyCallActive(_devices);

        public void NotifyEnded() => _call.NotifyCallEnded();
    }
}
