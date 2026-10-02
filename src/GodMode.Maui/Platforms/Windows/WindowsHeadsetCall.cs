using System.Runtime.Versioning;
using GodMode.Voice;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel.Calls;
using Windows.Devices.Enumeration;

namespace GodMode.Maui;

/// <summary>
/// The VoIP call held while voice's mic is open (issue #423), from the headset spike's <c>VoipCalls</c> (#382): its
/// call control device is the headset (Windows 11 24H2's <c>VoipCallCoordinator</c> call control devices,
/// <c>NotifyCallActive(deviceIds)</c>), so the headset's button in HFP, which is no media button there, comes back as the
/// call's <c>EndRequested</c>. Windows shows a call in the tray meanwhile. The unpackaged app needs no capability for it
/// (the spike's fourth trial). Below 26100 there is none (<see cref="Create"/>), and nothing here is called.
/// </summary>
public sealed class WindowsHeadsetCall : IHeadsetCall
{
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private VoipCallCoordinator? _coordinator;
    private VoipPhoneCall? _call;
    // Each End moves it on, so a start still under way ends the call it makes
    private int _round;
    private bool _disposed;

    private WindowsHeadsetCall(ILogger logger) => _logger = logger;

    [SupportedOSPlatformGuard("windows10.0.26100.0")]
    private static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100);

    /// <summary>The headset's call, from Windows 11 24H2 (10.0.26100) on; null before it.</summary>
    public static IHeadsetCall? Create(ILogger logger)
    {
        if (Supported) return new WindowsHeadsetCall(logger);
        logger.LogInformation("Voice: the headset's call needs Windows 11 24H2 (10.0.26100), this is {Version}: its button opens the mic only",
            Environment.OSVersion.Version);
        return null;
    }

    public event Action? EndRequested;

    public async Task StartAsync()
    {
        if (!Supported) return;
        int round;
        lock (_lock)
        {
            if (_disposed || _call is not null) return;
            round = _round;
        }

        var coordinator = _coordinator ??= VoipCallCoordinator.GetDefault();
        if (coordinator is null)
        {
            _logger.LogWarning("Voice: Windows has no VoIP call coordinator: the headset's button cannot close the mic");
            return;
        }
        // Afresh at each start: the headset may have connected since
        var devices = await DeviceInformation.FindAllAsync(VoipCallCoordinator.GetDeviceSelectorForCallControl());
        var ids = devices.Select(d => d.Id).ToList();
        if (ids.Count == 0)
        {
            _logger.LogInformation("Voice: no call control device: the headset's button cannot close the mic");
            return;
        }

        var call = coordinator.RequestNewOutgoingCall("godmode-voice", "GodMode", "GodMode voice", VoipPhoneCallMedia.Audio);
        call.EndRequested += (_, _) => Ended(call);
        call.NotifyCallActive(ids);
        lock (_lock)
        {
            if (!_disposed && round == _round)
            {
                _call = call;
                _logger.LogInformation("Voice: the headset's call is active, with {Devices}", string.Join(", ", devices.Select(d => d.Name)));
                return;
            }
        }
        // Ended while it started
        call.NotifyCallEnded();
    }

    /// <summary>The headset's button: Windows wants the call ended within 5 s, so it ends now, and the mic closes after.</summary>
    private void Ended(VoipPhoneCall call)
    {
        lock (_lock)
        {
            if (_call != call) return;
            _call = null;
        }
        NotifyEnded(call);
        _logger.LogInformation("Voice: the headset's button ended the call");
        EndRequested?.Invoke();
    }

    public void End()
    {
        VoipPhoneCall? call;
        lock (_lock)
        {
            _round++;
            call = _call;
            _call = null;
        }
        if (call is not null) NotifyEnded(call);
    }

    private void NotifyEnded(VoipPhoneCall call)
    {
        try
        {
            call.NotifyCallEnded();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice: the headset's call did not end cleanly");
        }
    }

    public void Dispose()
    {
        lock (_lock) _disposed = true;
        End();
    }
}
