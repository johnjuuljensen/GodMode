using System.Collections.Concurrent;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace GodMode.HeadsetSpike;

/// <summary>
/// Windows' audio endpoints, watched for the headset's profile. A Bluetooth Classic headset is a stereo endpoint
/// (A2DP) and a hands-free one (HFP: "Hands-Free" in its name), or on newer Windows 11 one unified endpoint that
/// switches inside. No API names the profile in use, so the spike logs everything that could show it: endpoints
/// coming, going and changing state, defaults and property changes (Core Audio's notifications), each headset
/// endpoint's mix format, and which of them carries sound (its peak meter). <see cref="Profile"/> is a guess from
/// those; the user's ears are the check.
/// </summary>
public sealed class Endpoints : IMMNotificationClient, IDisposable
{
    private const string Source = "AUDIO";
    private const float Audible = 0.0005f;
    private const int SilentPolls = 3;

    private readonly SpikeLog _log;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly ConcurrentDictionary<string, string> _formats = new();
    private readonly ConcurrentDictionary<string, int> _silentFor = new();
    private readonly ConcurrentDictionary<string, bool> _audible = new();
    private volatile IReadOnlyList<MMDevice> _headset = [];

    public Endpoints(SpikeLog log)
    {
        _log = log;
        _enumerator.RegisterEndpointNotificationCallback(this);
    }

    /// <summary>Endpoints whose name has this in it are the headset's (case-insensitive).</summary>
    public string HeadsetName { get; set; } = "OpenRun";

    /// <summary>The microphone the spike has open, or null; with the headset's, the headset is in HFP.</summary>
    public string? OpenMicrophoneId { get; set; }

    /// <summary>Logs every endpoint, and picks the headset's out again.</summary>
    public void Survey()
    {
        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.All))
            _log.Write(Source, $"endpoint {Describe(device)}");
        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
        foreach (var role in new[] { Role.Console, Role.Communications })
            _log.Write(Source, $"default {flow} {role}: {Name(DefaultId(flow, role))}");
        Rescan();
    }

    private string? DefaultId(DataFlow flow, Role role)
    {
        try
        {
            return _enumerator.GetDefaultAudioEndpoint(flow, role).ID;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Rescan()
    {
        _headset = [.. _enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active)
            .Where(d => d.FriendlyName.Contains(HeadsetName, StringComparison.OrdinalIgnoreCase))];
        _log.Write(Source, $"headset endpoints ('{HeadsetName}'): [{string.Join("; ", _headset.Select(d => $"{d.DataFlow} '{d.FriendlyName}'"))}]");
        PollFormats();
    }

    private static string Describe(MMDevice device)
    {
        var name = device.State == DeviceState.Active ? device.FriendlyName : SafeName(device);
        return $"{device.DataFlow} {device.State} '{name}' {device.ID}";
    }

    private static string SafeName(MMDevice device)
    {
        try
        {
            return device.FriendlyName;
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private string Name(string? id)
    {
        if (id is null) return "(none)";
        try
        {
            return $"'{_enumerator.GetDevice(id).FriendlyName}'";
        }
        catch (Exception)
        {
            return id;
        }
    }

    /// <summary>Every 100 ms: which headset endpoints carry sound. A change is logged, silence only after 300 ms of it.</summary>
    public void PollMeters()
    {
        foreach (var device in _headset)
        {
            float peak;
            try
            {
                peak = device.AudioMeterInformation.MasterPeakValue;
            }
            catch (Exception)
            {
                continue;
            }
            var id = device.ID;
            var was = _audible.GetValueOrDefault(id);
            if (peak > Audible)
            {
                _silentFor[id] = 0;
                if (!was)
                {
                    _audible[id] = true;
                    _log.Write(Source, $"sound on {device.DataFlow} '{device.FriendlyName}' (peak {peak:F4})");
                }
            }
            else if (was && _silentFor.AddOrUpdate(id, 1, (_, n) => n + 1) >= SilentPolls)
            {
                _audible[id] = false;
                _log.Write(Source, $"silent on {device.DataFlow} '{device.FriendlyName}'");
            }
        }
    }

    /// <summary>Every second, and on any change: each headset endpoint's shared-mode mix format, logged when it changes.</summary>
    public void PollFormats()
    {
        foreach (var device in _headset)
        {
            string format;
            try
            {
                var client = device.AudioClient;
                var mix = client.MixFormat;
                format = $"{mix.SampleRate} Hz, {mix.Channels} ch, {mix.BitsPerSample} bit";
                client.Dispose();
            }
            catch (Exception ex)
            {
                format = $"(unreadable: {ex.Message})";
            }
            var id = device.ID;
            if (_formats.TryGetValue(id, out var was) && was == format) continue;
            _formats[id] = format;
            _log.Write(Source, $"mix format of {device.DataFlow} '{device.FriendlyName}': {format}");
        }
    }

    /// <summary>The headset's profile as far as the endpoints tell, with why.</summary>
    public string Profile
    {
        get
        {
            var headset = _headset;
            if (headset.Count == 0) return $"no active endpoint named '{HeadsetName}'";
            static bool HandsFree(MMDevice d) => d.FriendlyName.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase);
            var audible = headset.Where(d => d.DataFlow == DataFlow.Render && _audible.GetValueOrDefault(d.ID)).ToList();
            if (OpenMicrophoneId is { } mic && headset.Any(d => d.ID == mic))
                return "HFP? (the headset's microphone is open)";
            if (audible.Any(HandsFree)) return "HFP? (sound on the hands-free endpoint)";
            if (audible.Count > 0) return $"A2DP? (sound on '{audible[0].FriendlyName}')";
            return "A2DP? (idle, microphone closed)";
        }
    }

    /// <summary>Each headset endpoint, its format and whether it carries sound, for the state panel.</summary>
    public string Detail => string.Join(Environment.NewLine, _headset.Select(d =>
        $"{d.DataFlow} '{d.FriendlyName}': {_formats.GetValueOrDefault(d.ID, "?")}{(_audible.GetValueOrDefault(d.ID) ? ", sound" : "")}"));

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        _log.Write(Source, $"state changed: {Name(deviceId)} -> {newState}");
        RescanLater();
    }

    public void OnDeviceAdded(string pwstrDeviceId)
    {
        _log.Write(Source, $"added: {Name(pwstrDeviceId)}");
        RescanLater();
    }

    public void OnDeviceRemoved(string deviceId)
    {
        _log.Write(Source, $"removed: {deviceId}");
        RescanLater();
    }

    // Off Core Audio's notification thread, which must not wait on the audio service
    private void RescanLater() => Task.Run(Rescan);

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) =>
        _log.Write(Source, $"default {flow} {role} -> {Name(defaultDeviceId)}");

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
        // Only the headset's: other devices change properties all the time
        if (_headset.All(d => d.ID != pwstrDeviceId)) return;
        _log.Write(Source, $"property {key.formatId},{key.propertyId} changed on {Name(pwstrDeviceId)}");
        Task.Run(PollFormats);
    }

    public void Dispose()
    {
        _enumerator.UnregisterEndpointNotificationCallback(this);
        _enumerator.Dispose();
    }
}
