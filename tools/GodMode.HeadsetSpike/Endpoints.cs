using System.Collections.Concurrent;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace GodMode.HeadsetSpike;

/// <summary>
/// Windows' audio endpoints, watched for the headset's profile. A Bluetooth Classic headset is a stereo endpoint
/// (A2DP: "Headphones") and a hands-free one (HFP: "Headset", "Hands-Free" on Windows 10), or on newer Windows 11 one unified endpoint that
/// switches inside. No API names the profile in use, so the spike logs everything that could show it: endpoints
/// coming, going and changing state, defaults and property changes (Core Audio's notifications), each headset
/// endpoint's mix format, and which of them carries sound (its peak meter). <see cref="Profile"/> is a guess from
/// those; the user's ears are the check.
/// </summary>
/// <remarks>
/// Core Audio objects stay on background (MTA) threads: NAudio's devices fail across apartments (DataFlow is a
/// NullReferenceException on the STA UI thread), so the enumerator is made there, meters are polled there, and the UI
/// reads only <see cref="HeadsetEndpoint"/> snapshots.
/// </remarks>
public sealed class Endpoints : IMMNotificationClient, IDisposable
{
    private const string Source = "AUDIO";
    private const float Audible = 0.0005f;
    private const int SilentPolls = 3;

    private readonly SpikeLog _log;
    private readonly MMDeviceEnumerator _enumerator;
    private readonly ConcurrentDictionary<string, string> _formats = new();
    private readonly ConcurrentDictionary<string, int> _silentFor = new();
    private readonly ConcurrentDictionary<string, bool> _audible = new();
    private readonly ConcurrentDictionary<string, AudioEndpointVolume> _volumes = new();
    private volatile IReadOnlyList<HeadsetEndpoint> _headset = [];

    /// <summary>
    /// A headset render endpoint's volume changed. The trials found Windows keeps a volume per profile, so with no hand
    /// on the volume this marks a switch: HFP's volume 0.2–0.4 s after the mic opens, A2DP's 5.2–5.3 s after it closes.
    /// </summary>
    public event Action? RenderVolumeChanged;
    private int _polling;

    /// <summary>A headset endpoint: the device (for background threads only) and what the UI reads of it.</summary>
    private sealed record HeadsetEndpoint(MMDevice Device, string Id, string Name, DataFlow Flow)
    {
        // Windows 10 names the HFP speaker "Headset (... Hands-Free AG Audio)"; Windows 11 "Headset (...)", beside "Headphones (...)" for A2DP
        public bool HandsFree =>
            Name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase) || Name.StartsWith("Headset", StringComparison.OrdinalIgnoreCase);
    }

    public Endpoints(SpikeLog log)
    {
        _log = log;
        _enumerator = Task.Run(() =>
        {
            var enumerator = new MMDeviceEnumerator();
            enumerator.RegisterEndpointNotificationCallback(this);
            return enumerator;
        }).Result;
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
            .Where(d => d.FriendlyName.Contains(HeadsetName, StringComparison.OrdinalIgnoreCase))
            .Select(d => new HeadsetEndpoint(d, d.ID, d.FriendlyName, d.DataFlow))];
        _log.Write(Source, $"headset endpoints ('{HeadsetName}'): [{string.Join("; ", _headset.Select(e => $"{e.Flow} '{e.Name}'"))}]");
        WatchVolumes();
        PollFormats();
    }

    /// <summary>
    /// Each headset endpoint's volume and mute, logged at first and on every change: the headset's volume buttons may
    /// reach Windows only as a volume change (AVRCP absolute volume), not as keys.
    /// </summary>
    private void WatchVolumes()
    {
        // An endpoint that went away is watched afresh when it comes back
        foreach (var gone in _volumes.Keys.Where(id => _headset.All(e => e.Id != id)).ToList())
            _volumes.TryRemove(gone, out _);
        foreach (var endpoint in _headset)
        {
            if (_volumes.ContainsKey(endpoint.Id)) continue;
            try
            {
                var volume = endpoint.Device.AudioEndpointVolume;
                if (!_volumes.TryAdd(endpoint.Id, volume)) continue;
                _log.Write(Source, $"volume of {endpoint.Flow} '{endpoint.Name}': {volume.MasterVolumeLevelScalar:P0}{(volume.Mute ? ", muted" : "")}");
                var last = $"{volume.MasterVolumeLevelScalar:P0}{(volume.Mute ? ", muted" : "")}";
                // Windows notifies once per channel too: only a change of the master volume or mute is logged
                volume.OnVolumeNotification += data =>
                {
                    var now = $"{data.MasterVolume:P0}{(data.Muted ? ", muted" : "")}";
                    if (Interlocked.Exchange(ref last, now) == now) return;
                    _log.Write(Source, $"volume of {endpoint.Flow} '{endpoint.Name}' -> {now}");
                    if (endpoint.Flow == DataFlow.Render) RenderVolumeChanged?.Invoke();
                };
            }
            catch (Exception ex)
            {
                _log.Error(Source, $"volume of '{endpoint.Name}'", ex);
            }
        }
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

    /// <summary>Every 100 ms, from the UI's timer: polls the meters on a background thread, skipped while the last poll runs.</summary>
    public void PollMetersInBackground()
    {
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;
        Task.Run(() =>
        {
            try
            {
                PollMeters();
            }
            finally
            {
                Volatile.Write(ref _polling, 0);
            }
        });
    }

    /// <summary>Which headset endpoints carry sound. A change is logged, silence only after 300 ms of it.</summary>
    private void PollMeters()
    {
        foreach (var endpoint in _headset)
        {
            float peak;
            try
            {
                peak = endpoint.Device.AudioMeterInformation.MasterPeakValue;
            }
            catch (Exception)
            {
                continue;
            }
            var id = endpoint.Id;
            var was = _audible.GetValueOrDefault(id);
            if (peak > Audible)
            {
                _silentFor[id] = 0;
                if (!was)
                {
                    _audible[id] = true;
                    _log.Write(Source, $"sound on {endpoint.Flow} '{endpoint.Name}' (peak {peak:F4})");
                }
            }
            else if (was && _silentFor.AddOrUpdate(id, 1, (_, n) => n + 1) >= SilentPolls)
            {
                _audible[id] = false;
                _log.Write(Source, $"silent on {endpoint.Flow} '{endpoint.Name}'");
            }
        }
    }

    /// <summary>Every second, and on any change (on a background thread): each headset endpoint's mix format, logged when it changes.</summary>
    public void PollFormats()
    {
        foreach (var endpoint in _headset)
        {
            string format;
            try
            {
                var client = endpoint.Device.AudioClient;
                var mix = client.MixFormat;
                format = $"{mix.SampleRate} Hz, {mix.Channels} ch, {mix.BitsPerSample} bit";
                client.Dispose();
            }
            catch (Exception ex)
            {
                format = $"(unreadable: {ex.Message})";
            }
            if (_formats.TryGetValue(endpoint.Id, out var was) && was == format) continue;
            _formats[endpoint.Id] = format;
            _log.Write(Source, $"mix format of {endpoint.Flow} '{endpoint.Name}': {format}");
        }
    }

    /// <summary>The headset's profile as far as the endpoints tell, with why. Reads snapshots only: the UI thread calls it.</summary>
    public string Profile
    {
        get
        {
            var headset = _headset;
            if (headset.Count == 0) return $"no active endpoint named '{HeadsetName}'";
            var audible = headset.Where(e => e.Flow == DataFlow.Render && _audible.GetValueOrDefault(e.Id)).ToList();
            if (OpenMicrophoneId is { } mic && headset.Any(e => e.Id == mic))
                return "HFP? (the headset's microphone is open)";
            if (audible.Any(e => e.HandsFree)) return "HFP? (sound on the hands-free endpoint)";
            if (audible.Count > 0) return $"A2DP? (sound on '{audible[0].Name}')";
            return "A2DP? (idle, microphone closed)";
        }
    }

    /// <summary>Each headset endpoint, its format and whether it carries sound, for the state panel (snapshots only).</summary>
    public string Detail => string.Join(Environment.NewLine, _headset.Select(e =>
        $"{e.Flow} '{e.Name}': {_formats.GetValueOrDefault(e.Id, "?")}{(_audible.GetValueOrDefault(e.Id) ? ", sound" : "")}"));

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
        if (_headset.All(e => e.Id != pwstrDeviceId)) return;
        _log.Write(Source, $"property {key.formatId},{key.propertyId} changed on {Name(pwstrDeviceId)}");
        Task.Run(PollFormats);
    }

    public void Dispose()
    {
        _enumerator.UnregisterEndpointNotificationCallback(this);
        _enumerator.Dispose();
    }
}
