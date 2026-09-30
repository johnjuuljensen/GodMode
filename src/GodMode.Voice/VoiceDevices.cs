namespace GodMode.Voice;

/// <summary>A microphone or a speaker, as the platform names it: its endpoint id and the name shown for it.</summary>
public sealed record AudioDevice(string Id, string Name);

/// <summary>
/// What <c>voice.devices</c> answers: the microphones and speakers there are now, and the default communications
/// device of each kind (null when there is none). <see cref="Supported"/> is false where voice picks its own route
/// (Android, which prefers a headset by itself), and the lists are empty.
/// </summary>
public sealed record VoiceDeviceList(
    bool Supported,
    IReadOnlyList<AudioDevice> Microphones,
    IReadOnlyList<AudioDevice> Speakers,
    string? DefaultMicrophoneId = null,
    string? DefaultSpeakerId = null)
{
    public static readonly VoiceDeviceList None = new(false, [], []);
}

/// <summary>The device to open: an id from the list, or none when there is no device of that kind at all.</summary>
/// <param name="Id">The endpoint to open; null when there is neither the pinned device nor a default.</param>
/// <param name="Name">Its name, for the log.</param>
/// <param name="Pinned">It is the device the settings chose, not the default.</param>
/// <param name="FellBack">The settings chose a device that is not there, so this is the default in its place.</param>
public sealed record DeviceChoice(string? Id, string Name, bool Pinned, bool FellBack);

public static class VoiceDevices
{
    /// <summary>
    /// The device a setting means now: the pinned one while it is there, else the default. Picked again whenever the
    /// devices change, so a pinned device that comes back is used again.
    /// </summary>
    /// <param name="pinned">The setting: null for Default.</param>
    public static DeviceChoice Choose(AudioDevice? pinned, IReadOnlyList<AudioDevice> present, string? defaultId)
    {
        if (pinned is not null && present.FirstOrDefault(d => SameId(d.Id, pinned.Id)) is { } found)
            return new DeviceChoice(found.Id, found.Name, Pinned: true, FellBack: false);

        var fellBack = pinned is not null;
        return defaultId is not null && present.FirstOrDefault(d => SameId(d.Id, defaultId)) is { } byDefault
            ? new DeviceChoice(byDefault.Id, byDefault.Name, Pinned: false, fellBack)
            : new DeviceChoice(null, "none", Pinned: false, fellBack);
    }

    /// <summary>Endpoint ids compare without case, the way Windows' own do.</summary>
    public static bool SameId(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
