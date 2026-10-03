namespace GodMode.Maui;

/// <summary>
/// Whether this device makes a sound for what interrupts (issue #438): an item whose session is important
/// (<see cref="Shared.Enums.AttentionAlert.Interrupt"/>). The device's own setting, not the session's: a desktop may chime
/// while the phone stays silent at night. On by default; the app's settings change it (<c>attention.sound.set</c>).
/// </summary>
public static class AttentionSound
{
    private const string Key = "attention.sound";

    public static bool Enabled
    {
        get => Preferences.Default.Get(Key, true);
        set => Preferences.Default.Set(Key, value);
    }
}
