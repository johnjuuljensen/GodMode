using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace GodMode.HeadsetSpike;

/// <summary>
/// Whether the headset offers Bluetooth LE Audio. Windows has no public API that says which codec or transport an
/// audio endpoint uses; what an app can see is the paired device itself. An LE Audio headset is a paired Bluetooth LE
/// device with the LE Audio GATT services: Published Audio Capabilities (0x1850) and Audio Stream Control (0x184E),
/// usually with Common Audio (0x1853) and Telephony and Media Audio (0x1855). A Classic-only headset is a paired
/// Bluetooth Classic device with no LE twin, or an LE one without those services.
/// </summary>
public sealed class LeAudioProbe(SpikeLog log)
{
    private const string Source = "LEAUD";

    private static readonly Dictionary<ushort, string> LeAudioServices = new()
    {
        [0x184E] = "Audio Stream Control",
        [0x184F] = "Broadcast Audio Scan",
        [0x1850] = "Published Audio Capabilities",
        [0x1853] = "Common Audio",
        [0x1854] = "Hearing Access",
        [0x1855] = "Telephony and Media Audio",
    };

    public async Task RunAsync(string name)
    {
        log.Write(Source, $"probing paired Bluetooth devices named '{name}'");
        try
        {
            foreach (var info in await Paired(BluetoothDevice.GetDeviceSelectorFromPairingState(true), name))
            {
                using var device = await BluetoothDevice.FromIdAsync(info.Id);
                log.Write(Source, $"Classic '{info.Name}' {device?.BluetoothAddress:X12}: class {device?.ClassOfDevice.MajorClass}/{device?.ClassOfDevice.MinorClass}, {device?.ConnectionStatus}");
                if (device is null) continue;
                var services = await device.GetRfcommServicesAsync(BluetoothCacheMode.Cached);
                log.Write(Source, $"  RFCOMM services: [{string.Join(", ", services.Services.Select(s => s.ServiceId.Uuid))}]");
            }

            var found = false;
            foreach (var info in await Paired(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), name))
            {
                found = true;
                using var device = await BluetoothLEDevice.FromIdAsync(info.Id);
                if (device is null)
                {
                    log.Write(Source, $"LE '{info.Name}': cannot open");
                    continue;
                }
                var result = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
                log.Write(Source, $"LE '{info.Name}' {device.BluetoothAddress:X12}, {device.ConnectionStatus}: GATT {result.Status}");
                var leAudio = new List<string>();
                foreach (var service in result.Services ?? [])
                {
                    var shortId = ShortId(service.Uuid);
                    var known = shortId is { } s && LeAudioServices.TryGetValue(s, out var n) ? n : null;
                    if (known is not null) leAudio.Add(known);
                    log.Write(Source, $"  service {service.Uuid}{(known is null ? "" : $" ({known})")}");
                    service.Dispose();
                }
                log.Write(Source, leAudio.Count > 0
                    ? $"LE Audio services on '{info.Name}': {string.Join(", ", leAudio)}: it offers LE Audio"
                    : $"no LE Audio service on '{info.Name}'");
            }
            if (!found) log.Write(Source, $"no paired Bluetooth LE device named '{name}': no LE Audio, as far as Windows sees");
        }
        catch (Exception ex)
        {
            log.Error(Source, "probe", ex);
        }
    }

    private static async Task<IEnumerable<DeviceInformation>> Paired(string selector, string name) =>
        (await DeviceInformation.FindAllAsync(selector)).Where(d => d.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A Bluetooth SIG 16-bit id, for a UUID on the Bluetooth base (0000xxxx-0000-1000-8000-00805F9B34FB).</summary>
    private static ushort? ShortId(Guid uuid)
    {
        var text = uuid.ToString();
        return text.StartsWith("0000", StringComparison.Ordinal) && text.EndsWith("-0000-1000-8000-00805f9b34fb", StringComparison.Ordinal)
            ? Convert.ToUInt16(text[4..8], 16)
            : null;
    }
}
