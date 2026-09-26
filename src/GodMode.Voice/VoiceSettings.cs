using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using GodMode.ClientBase.Services;
using GodMode.Shared;
using VoiceBot.Core.Resources;

namespace GodMode.Voice;

/// <summary>The Claude model behind each of VoiceBot's inference tiers.</summary>
public sealed record VoiceModels(string Light, string Medium, string Heavy)
{
    public static readonly VoiceModels Default = new("claude-haiku-4-5-20251001", "claude-sonnet-5", "claude-opus-5-5");
}

/// <summary>
/// The voice settings that are not secret, kept in <c>voice.json</c> beside the app's other files
/// (<see cref="GodMode.ClientBase.GodModePaths.AppDataDirectory"/>). The keys are in <see cref="ISecretStore"/>.
/// </summary>
public sealed record VoiceSettings
{
    /// <summary>Danish replies, with English mixed into what the user says.</summary>
    public const string DefaultLanguage = "da-DK+en";

    /// <summary>The ElevenLabs voice VoiceBot's own clients use.</summary>
    public const string DefaultVoiceId = "xj6X4BCUsv9oxohm1E8o";

    /// <summary>The session's languages, in VoiceBot's form: the primary, then each mixed-in one after a '+'.</summary>
    public string Language { get; init; } = DefaultLanguage;

    public string VoiceId { get; init; } = DefaultVoiceId;

    /// <summary>
    /// Windows' Voice Capture DSP instead of the plain microphone. Off until it is measured to hold up on laptop
    /// speakers (johnjuuljensen/VoiceBot#18): until then, use a headset.
    /// </summary>
    public bool EchoCancellation { get; init; }

    public VoiceModels Models { get; init; } = VoiceModels.Default;

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

/// <summary>The API keys voice needs, as read from <see cref="ISecretStore"/>.</summary>
public sealed record VoiceKeys(string? ElevenLabs, string? Anthropic);

/// <summary>What <c>voice.settings.get</c> shows: the settings, and whether each key is set. Never a key itself.</summary>
public sealed record VoiceSettingsView(
    string Language,
    string VoiceId,
    bool EchoCancellation,
    VoiceModels Models,
    bool ElevenLabsKeySet,
    bool AnthropicKeySet);

/// <summary>
/// What <c>voice.settings.set</c> changes: a null field is left as it is. A key that is blank removes the key.
/// </summary>
public sealed record VoiceSettingsUpdate(
    string? Language = null,
    string? VoiceId = null,
    bool? EchoCancellation = null,
    VoiceModels? Models = null,
    string? ElevenLabsKey = null,
    string? AnthropicKey = null);

/// <summary>Reads and writes the voice settings: <c>voice.json</c> in a directory, the keys in secure storage.</summary>
public sealed class VoiceSettingsStore(string directory, ISecretStore secrets)
{
    public const string ElevenLabsKeyName = "voice.elevenlabs-api-key";
    public const string AnthropicKeyName = "voice.anthropic-api-key";
    public const string FileName = "voice.json";

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

    /// <summary>The keys. One that secure storage cannot read (the store was reset, say) is the same as none.</summary>
    public async Task<VoiceKeys> LoadKeysAsync() =>
        new(await ReadKeyAsync(ElevenLabsKeyName), await ReadKeyAsync(AnthropicKeyName));

    public async Task<VoiceSettingsView> GetViewAsync()
    {
        var settings = await LoadAsync();
        var keys = await LoadKeysAsync();
        return new VoiceSettingsView(settings.Language, settings.VoiceId, settings.EchoCancellation, settings.Models,
            ElevenLabsKeySet: !string.IsNullOrEmpty(keys.ElevenLabs),
            AnthropicKeySet: !string.IsNullOrEmpty(keys.Anthropic));
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
                Models = update.Models ?? current.Models,
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

    /// <summary>Blank fields (a hand-edited file, an empty text box) take their defaults.</summary>
    private static VoiceSettings Normalized(VoiceSettings settings) => settings with
    {
        Language = Or(settings.Language, VoiceSettings.DefaultLanguage),
        VoiceId = Or(settings.VoiceId, VoiceSettings.DefaultVoiceId),
        Models = settings.Models is { } models
            ? new VoiceModels(Or(models.Light, VoiceModels.Default.Light), Or(models.Medium, VoiceModels.Default.Medium), Or(models.Heavy, VoiceModels.Default.Heavy))
            : VoiceModels.Default,
    };

    private static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private async Task<string?> ReadKeyAsync(string name)
    {
        try
        {
            return await secrets.GetAsync(name) is { Length: > 0 } key ? key : null;
        }
        catch (Exception)
        {
            secrets.Remove(name);
            return null;
        }
    }

    private async Task WriteKeyAsync(string name, string? value)
    {
        if (value is null) return;
        if (string.IsNullOrWhiteSpace(value))
            secrets.Remove(name);
        else
            await secrets.SetAsync(name, value.Trim());
    }
}
