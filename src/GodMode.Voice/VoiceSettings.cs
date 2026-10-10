using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GodMode.ClientBase.Services;
using GodMode.Shared;
using Microsoft.Extensions.Logging;
using VoiceBot.Core.Resources;

namespace GodMode.Voice;

/// <summary>
/// The voice settings that are not secret, kept in <c>voice.json</c> beside the app's other files
/// (<see cref="GodMode.ClientBase.GodModePaths.AppDataDirectory"/>). The keys are in <see cref="ISecretStore"/>. The
/// models are VoiceBot's (<see cref="CloudVoiceProviders.TierMap"/>): a file's old <c>Models</c> is ignored, and dropped
/// at its next save.
/// </summary>
public sealed record VoiceSettings
{
    /// <summary>Danish replies, with English mixed into what the user says.</summary>
    public const string DefaultLanguage = "da-DK+en";

    /// <summary>
    /// GodMode's ElevenLabs voice, godmode-2 (#502). A saved voice.json that names a voice keeps it, the old default
    /// included (no migration); Settings → Voice changes it.
    /// </summary>
    public const string DefaultVoiceId = "aDMeK4SvMKPrfK2biNvZ";

    /// <summary>The session's languages, in VoiceBot's form: the primary, then each mixed-in one after a '+'.</summary>
    public string Language { get; init; } = DefaultLanguage;

    public string VoiceId { get; init; } = DefaultVoiceId;

    /// <summary>
    /// Windows' Voice Capture DSP instead of the plain microphone. Off until it is measured to hold up on laptop
    /// speakers (johnjuuljensen/VoiceBot#18): until then, use a headset.
    /// </summary>
    public bool EchoCancellation { get; init; }

    /// <summary>The microphone voice uses: null for Default, which follows Windows' default communications microphone.</summary>
    public AudioDevice? Microphone { get; init; }

    /// <summary>
    /// The speaker voice uses: null for Default, which follows Windows' default device while the mic is closed (a
    /// Bluetooth headset's A2DP) and its default communications device while it is open.
    /// </summary>
    public AudioDevice? Speaker { get; init; }

    /// <summary>How many seconds of silence while voice listens close the mic (<see cref="VoiceMicOptions.SilenceTimeout"/>).</summary>
    public int MicSilenceSeconds { get; init; } = VoiceMicOptions.DefaultSilenceSeconds;

    /// <summary>
    /// Voice's short sounds: the "heard you" tone as what the user said is taken as a turn (<see cref="GodMode.Voice.Earcons.Heard"/>,
    /// #458), and the earcons before announcements (<see cref="Earcon"/>, #455). On by default; off plays neither. The mic's
    /// rising and falling tones are not among them: they say the mic opened or closed, and always play.
    /// </summary>
    public bool Earcons { get; init; } = true;

    /// <summary>How many hours without activity make a session stale by default (#468): a day.</summary>
    public const int DefaultStaleHours = 24;

    /// <summary>
    /// How many hours without activity leave a session out of voice's lists unless the user asks for all (#468): it is
    /// counted ("og 9 gamle"), not named. A session that needs the user is never left out so.
    /// </summary>
    public int StaleHours { get; init; } = DefaultStaleHours;

    /// <summary><see cref="StaleHours"/> as a span.</summary>
    [JsonIgnore]
    public TimeSpan StaleAfter => TimeSpan.FromHours(StaleHours);

    public static readonly VoiceSettings Default = new();

    /// <summary>The session's languages from <see cref="Language"/>.</summary>
    [JsonIgnore]
    public SessionLanguages Languages => ParseLanguages(Language);

    /// <exception cref="ArgumentException">Not VoiceBot's form, or a language .NET does not know.</exception>
    public static SessionLanguages ParseLanguages(string value)
    {
        try
        {
            var languages = SessionLanguages.Parse(value);
            foreach (var language in languages.All)
                _ = CultureInfo.GetCultureInfo(language, predefinedOnly: true);
            return languages;
        }
        catch (Exception ex) when (ex is FormatException or CultureNotFoundException or ArgumentException)
        {
            throw new ArgumentException($"Not a language setting: '{value}'. Give the reply language, then each mixed-in one after a '+', e.g. da-DK+en.", ex);
        }
    }
}

/// <summary>The API keys voice needs, as read from <see cref="ISecretStore"/>. Printed, it says only whether each is set.</summary>
public sealed record VoiceKeys(string? ElevenLabs, string? Anthropic)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"ElevenLabs = {VoiceSettingsStore.Shown(ElevenLabs)}, Anthropic = {VoiceSettingsStore.Shown(Anthropic)}");
        return true;
    }
}

/// <summary>What <c>voice.settings.get</c> shows: the settings, and whether each key is set. Never a key itself.</summary>
public sealed record VoiceSettingsView(
    string Language,
    string VoiceId,
    bool EchoCancellation,
    AudioDevice? Microphone,
    AudioDevice? Speaker,
    int MicSilenceSeconds,
    bool ElevenLabsKeySet,
    bool AnthropicKeySet,
    int StaleHours = VoiceSettings.DefaultStaleHours,
    bool Earcons = true);

/// <summary>
/// What <c>voice.settings.set</c> changes: a null field is left as it is. A key that is blank removes the key, and a
/// device whose id is blank chooses Default. Printed, it says only whether each key is set.
/// </summary>
public sealed record VoiceSettingsUpdate(
    string? Language = null,
    string? VoiceId = null,
    bool? EchoCancellation = null,
    AudioDevice? Microphone = null,
    AudioDevice? Speaker = null,
    string? ElevenLabsKey = null,
    string? AnthropicKey = null,
    int? MicSilenceSeconds = null,
    int? StaleHours = null,
    bool? Earcons = null)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Language = {Language}, VoiceId = {VoiceId}, EchoCancellation = {EchoCancellation}, Microphone = {Microphone}, ")
            .Append($"Speaker = {Speaker}, ElevenLabsKey = {VoiceSettingsStore.Shown(ElevenLabsKey)}, AnthropicKey = {VoiceSettingsStore.Shown(AnthropicKey)}, ")
            .Append($"MicSilenceSeconds = {MicSilenceSeconds}, StaleHours = {StaleHours}, Earcons = {Earcons}");
        return true;
    }
}

/// <summary>Reads and writes the voice settings: <c>voice.json</c> in a directory, the keys in secure storage.</summary>
public sealed class VoiceSettingsStore(string directory, ISecretStore secrets, ILogger? logger = null)
{
    public const string ElevenLabsKeyName = "voice.elevenlabs-api-key";
    public const string AnthropicKeyName = "voice.anthropic-api-key";
    public const string FileName = "voice.json";

    /// <summary>The longest silence timeout: an hour.</summary>
    public const int MaxMicSilenceSeconds = 3600;

    /// <summary>The longest a session may go without activity before voice leaves it out: a year.</summary>
    public const int MaxStaleHours = 24 * 365;

    private readonly SemaphoreSlim _writing = new(1, 1);

    private string FilePath => Path.Combine(directory, FileName);

    /// <summary>The saved settings, or the defaults for a file that is missing or unreadable.</summary>
    public async Task<VoiceSettings> LoadAsync()
    {
        try
        {
            await using var file = File.OpenRead(FilePath);
            return Normalized(await JsonSerializer.DeserializeAsync<VoiceSettings>(file, JsonDefaults.Options) ?? VoiceSettings.Default);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return VoiceSettings.Default;
        }
    }

    /// <summary>
    /// The keys. One that secure storage cannot read is the same as none, this time: it is logged and left where it is,
    /// as a server's token is, so a passing failure costs no key. A store that was reset holds none to read anyway.
    /// </summary>
    public async Task<VoiceKeys> LoadKeysAsync() =>
        new(await ReadKeyAsync(ElevenLabsKeyName), await ReadKeyAsync(AnthropicKeyName));

    public async Task<VoiceSettingsView> GetViewAsync()
    {
        var settings = await LoadAsync();
        var keys = await LoadKeysAsync();
        return new VoiceSettingsView(settings.Language, settings.VoiceId, settings.EchoCancellation, settings.Microphone, settings.Speaker,
            settings.MicSilenceSeconds,
            ElevenLabsKeySet: !string.IsNullOrEmpty(keys.ElevenLabs),
            AnthropicKeySet: !string.IsNullOrEmpty(keys.Anthropic),
            StaleHours: settings.StaleHours,
            Earcons: settings.Earcons);
    }

    /// <summary>Applies <paramref name="update"/> and returns what is set now.</summary>
    /// <exception cref="ArgumentException">The language is not VoiceBot's form (e.g. "da-DK+en").</exception>
    public async Task<VoiceSettingsView> UpdateAsync(VoiceSettingsUpdate update)
    {
        if (update.Language is { } language)
            VoiceSettings.ParseLanguages(language);

        await _writing.WaitAsync();
        try
        {
            var current = await LoadAsync();
            var next = Normalized(current with
            {
                Language = update.Language ?? current.Language,
                VoiceId = update.VoiceId ?? current.VoiceId,
                EchoCancellation = update.EchoCancellation ?? current.EchoCancellation,
                Microphone = update.Microphone ?? current.Microphone,
                Speaker = update.Speaker ?? current.Speaker,
                MicSilenceSeconds = update.MicSilenceSeconds ?? current.MicSilenceSeconds,
                StaleHours = update.StaleHours ?? current.StaleHours,
                Earcons = update.Earcons ?? current.Earcons,
            });
            if (next != current)
            {
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(FilePath, JsonSerializer.Serialize(next, JsonDefaults.Options));
            }

            await WriteKeyAsync(ElevenLabsKeyName, update.ElevenLabsKey);
            await WriteKeyAsync(AnthropicKeyName, update.AnthropicKey);
        }
        finally
        {
            _writing.Release();
        }
        return await GetViewAsync();
    }

    /// <summary>
    /// Blank fields (a hand-edited file, an empty text box) take their defaults; a device with a blank id is Default; a
    /// silence timeout of no seconds is the default, and one over an hour is an hour.
    /// </summary>
    private static VoiceSettings Normalized(VoiceSettings settings) => settings with
    {
        Language = Or(settings.Language, VoiceSettings.DefaultLanguage),
        VoiceId = Or(settings.VoiceId, VoiceSettings.DefaultVoiceId),
        Microphone = Device(settings.Microphone),
        Speaker = Device(settings.Speaker),
        MicSilenceSeconds = settings.MicSilenceSeconds > 0 ? Math.Min(settings.MicSilenceSeconds, MaxMicSilenceSeconds) : VoiceMicOptions.DefaultSilenceSeconds,
        StaleHours = settings.StaleHours > 0 ? Math.Min(settings.StaleHours, MaxStaleHours) : VoiceSettings.DefaultStaleHours,
    };

    private static AudioDevice? Device(AudioDevice? device) =>
        string.IsNullOrWhiteSpace(device?.Id) ? null : new AudioDevice(device.Id.Trim(), Or(device.Name, device.Id));

    private static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private async Task<string?> ReadKeyAsync(string name)
    {
        try
        {
            return await secrets.GetAsync(name) is { Length: > 0 } key ? key : null;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Secure storage could not read {Key}; voice goes without it this time", name);
            return null;
        }
    }

    /// <summary>How a key is printed: whether it is set, never the key.</summary>
    internal static string Shown(string? key) => string.IsNullOrEmpty(key) ? "not set" : "set";

    private async Task WriteKeyAsync(string name, string? value)
    {
        if (value is null) return;
        if (string.IsNullOrWhiteSpace(value))
            secrets.Remove(name);
        else
            await secrets.SetAsync(name, value.Trim());
    }
}
