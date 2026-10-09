using System.Collections.Concurrent;
using System.Text.Json;
using GodMode.ClientBase.Services;
using GodMode.Shared;
using VoiceBot.AI;
using VoiceBot.Core.AI;

namespace GodMode.Voice.Tests;

/// <summary>The voice settings: in voice.json, but the keys only in secure storage, and never back to the page.</summary>
public sealed class VoiceSettingsTests : IDisposable
{
    private const string ElevenLabsKey = "sk_elevenlabs_0123456789abcdef";
    private const string AnthropicKey = "sk-ant-api03-0123456789abcdef";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"godmode-voice-settings-{Guid.NewGuid():N}");
    private readonly MemorySecrets _secrets = new();
    private readonly VoiceSettingsStore _store;

    public VoiceSettingsTests() => _store = new VoiceSettingsStore(_dir, _secrets);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public async Task The_defaults_are_Danish_with_English_and_no_echo_cancellation()
    {
        var view = await _store.GetViewAsync();

        Assert.Equal("da-DK+en", view.Language);
        Assert.Equal("OyYu1oFho6PvCH2wRY3S", view.VoiceId);
        Assert.False(view.EchoCancellation);
        Assert.False(view.ElevenLabsKeySet);
        Assert.False(view.AnthropicKeySet);
    }

    /// <summary>How long without activity leaves a session out of voice's lists (#468): a day, unless set; none or less is the day.</summary>
    [Fact]
    public async Task The_stale_hours_are_a_day_unless_set()
    {
        Assert.Equal(VoiceSettings.DefaultStaleHours, (await _store.GetViewAsync()).StaleHours);
        Assert.Equal(TimeSpan.FromHours(24), VoiceSettings.Default.StaleAfter);

        Assert.Equal(6, (await _store.UpdateAsync(new VoiceSettingsUpdate(StaleHours: 6))).StaleHours);
        Assert.Equal(TimeSpan.FromHours(6), (await new VoiceSettingsStore(_dir, _secrets).LoadAsync()).StaleAfter);
        Assert.Equal(VoiceSettings.DefaultStaleHours, (await _store.UpdateAsync(new VoiceSettingsUpdate(StaleHours: 0))).StaleHours);
    }

    /// <summary>The earcons and the "heard you" tone (#458) are on, in a file saved before the setting too, until turned off.</summary>
    [Fact]
    public async Task The_earcons_are_on_unless_turned_off()
    {
        Assert.True((await _store.GetViewAsync()).Earcons);
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, VoiceSettingsStore.FileName), """{ "Language": "en" }""");
        Assert.True((await _store.LoadAsync()).Earcons);

        Assert.False((await _store.UpdateAsync(new VoiceSettingsUpdate(Earcons: false))).Earcons);
        Assert.False((await new VoiceSettingsStore(_dir, _secrets).LoadAsync()).Earcons);
        Assert.False((await _store.UpdateAsync(new VoiceSettingsUpdate(StaleHours: 6))).Earcons);
        Assert.True((await _store.UpdateAsync(new VoiceSettingsUpdate(Earcons: true))).Earcons);
    }

    /// <summary>A file saved when voice kept its own models (#475) loads as it is, and its next save drops them.</summary>
    [Fact]
    public async Task A_file_with_the_old_models_loads_and_its_next_save_drops_them()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, VoiceSettingsStore.FileName);
        await File.WriteAllTextAsync(file,
            """{ "Language": "en", "Models": { "Light": "l", "Medium": "claude-sonnet-5", "Heavy": "h" } }""");

        Assert.Equal("en", (await _store.LoadAsync()).Language);

        await _store.UpdateAsync(new VoiceSettingsUpdate(VoiceId: "voice-2"));
        Assert.DoesNotContain("Models", await File.ReadAllTextAsync(file));
    }

    /// <summary>
    /// A file's old Models (before #475) still has no effect (#525): it is not the tier models, so every tier is
    /// VoiceBot's default.
    /// </summary>
    [Fact]
    public async Task A_file_with_the_old_models_leaves_every_tier_at_VoiceBots_default()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, VoiceSettingsStore.FileName),
            """{ "Models": { "Light": "l", "Medium": "claude-sonnet-5", "Heavy": "h" } }""");

        var settings = await _store.LoadAsync();

        Assert.Null(settings.TierModels);
        Assert.Equal(DefaultTierMap(), CloudVoiceProviders.TierMap(settings.TierModels));
    }

    /// <summary>A tier left empty is VoiceBot's default; one set is the model set, shown beside the default (#525).</summary>
    [Fact]
    public async Task An_unset_tier_is_VoiceBots_default_and_a_set_one_is_the_override()
    {
        var view = await _store.UpdateAsync(new VoiceSettingsUpdate(TierModels: new VoiceTierModels(Medium: " claude-haiku-5-5 ", Heavy: "  ")));
        var settings = await new VoiceSettingsStore(_dir, _secrets).LoadAsync();
        var tierMap = CloudVoiceProviders.TierMap(settings.TierModels);

        Assert.Equal(new VoiceTierModels(Medium: "claude-haiku-5-5"), view.TierModels);
        Assert.Equal(VoiceTierModels.Defaults, view.DefaultTierModels);
        Assert.Equal("claude-haiku-5-5", tierMap[InferenceTier.Medium].Model);
        Assert.Equal(TierMapConfiguration.DefaultModels[InferenceTier.Light], tierMap[InferenceTier.Light].Model);
        Assert.Equal(TierMapConfiguration.DefaultModels[InferenceTier.Heavy], tierMap[InferenceTier.Heavy].Model);
        Assert.All(tierMap.Values, t => Assert.Equal(TierMapConfiguration.DefaultProvider, t.Provider));
    }

    /// <summary>Only the tiers that are set are saved, so a default VoiceBot moves is followed; emptied again, none is.</summary>
    [Fact]
    public async Task Only_the_tiers_that_are_set_are_saved()
    {
        var file = Path.Combine(_dir, VoiceSettingsStore.FileName);

        await _store.UpdateAsync(new VoiceSettingsUpdate(TierModels: new VoiceTierModels(Medium: "claude-haiku-5-5")));
        var saved = await File.ReadAllTextAsync(file);
        Assert.Contains("\"TierModels\"", saved);
        Assert.Contains("\"Medium\": \"claude-haiku-5-5\"", saved);
        Assert.DoesNotContain("Light", saved);
        Assert.DoesNotContain("Heavy", saved);
        Assert.Equal(new VoiceTierModels(Medium: "claude-haiku-5-5"), (await _store.UpdateAsync(new VoiceSettingsUpdate(StaleHours: 6))).TierModels);

        var emptied = await _store.UpdateAsync(new VoiceSettingsUpdate(TierModels: new VoiceTierModels(Medium: "")));
        Assert.Null(emptied.TierModels);
        Assert.DoesNotContain("TierModels", await File.ReadAllTextAsync(file));
        Assert.Equal(DefaultTierMap(), CloudVoiceProviders.TierMap((await _store.LoadAsync()).TierModels));
    }

    private static Dictionary<InferenceTier, TierConfig> DefaultTierMap() =>
        TierMapConfiguration.DefaultModels.ToDictionary(d => d.Key, d => new TierConfig(TierMapConfiguration.DefaultProvider, d.Value));

    /// <summary>What voice.settings.get returns, as the page gets it: whether each key is set, never the key.</summary>
    [Fact]
    public async Task Keys_go_to_secure_storage_and_the_view_says_only_that_they_are_set()
    {
        var afterSet = await _store.UpdateAsync(new VoiceSettingsUpdate(VoiceId: "voice-2", ElevenLabsKey: ElevenLabsKey, AnthropicKey: $"  {AnthropicKey} "));
        var afterGet = await _store.GetViewAsync();

        Assert.True(afterGet.ElevenLabsKeySet);
        Assert.True(afterGet.AnthropicKeySet);
        Assert.Equal(afterGet, afterSet);
        foreach (var json in new[] { afterSet, afterGet }.Select(v => JsonSerializer.Serialize(v, JsonDefaults.Options)))
        {
            Assert.DoesNotContain(ElevenLabsKey, json);
            Assert.DoesNotContain(AnthropicKey, json);
        }
        Assert.Equal(new VoiceKeys(ElevenLabsKey, AnthropicKey), await _store.LoadKeysAsync());
        Assert.NotEmpty(Directory.GetFiles(_dir));
        Assert.All(Directory.GetFiles(_dir), f =>
        {
            var text = File.ReadAllText(f);
            Assert.DoesNotContain(ElevenLabsKey, text);
            Assert.DoesNotContain(AnthropicKey, text);
        });
    }

    [Fact]
    public async Task A_null_field_is_left_as_it_is_and_a_blank_key_removes_the_key()
    {
        await _store.UpdateAsync(new VoiceSettingsUpdate(VoiceId: "voice-2", EchoCancellation: true, ElevenLabsKey: ElevenLabsKey, AnthropicKey: AnthropicKey));

        var view = await _store.UpdateAsync(new VoiceSettingsUpdate(Language: "en-US", ElevenLabsKey: ""));

        Assert.Equal("en-US", view.Language);
        Assert.Equal("voice-2", view.VoiceId);
        Assert.True(view.EchoCancellation);
        Assert.False(view.ElevenLabsKeySet);
        Assert.True(view.AnthropicKeySet);
        Assert.Equal("en-US", (await new VoiceSettingsStore(_dir, _secrets).LoadAsync()).Language);
    }

    /// <summary>No migration: a voice.json saved before the default changed keeps the voice it names.</summary>
    [Fact]
    public async Task A_saved_voice_is_kept_whatever_the_default()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, VoiceSettingsStore.FileName), """{ "VoiceId": "xj6X4BCUsv9oxohm1E8o" }""");

        Assert.Equal("xj6X4BCUsv9oxohm1E8o", (await _store.GetViewAsync()).VoiceId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("dansk")]
    [InlineData("xx-NOPE+en")]
    public async Task A_language_that_is_not_one_is_refused(string language) =>
        await Assert.ThrowsAsync<ArgumentException>(() => _store.UpdateAsync(new VoiceSettingsUpdate(Language: language)));

    [Fact]
    public async Task The_devices_are_Default_until_one_is_chosen_and_a_blank_id_chooses_Default_again()
    {
        var headset = new AudioDevice("{0.0.1.00000000}.{headset}", "Headset (Hands-Free)");
        var speakers = new AudioDevice("{0.0.0.00000000}.{speakers}", "Speakers (Realtek)");

        Assert.Null((await _store.GetViewAsync()).Microphone);
        var chosen = await _store.UpdateAsync(new VoiceSettingsUpdate(Microphone: headset, Speaker: speakers));
        var reread = await new VoiceSettingsStore(_dir, _secrets).LoadAsync();
        var untouched = await _store.UpdateAsync(new VoiceSettingsUpdate(Language: "en-US"));
        var backToDefault = await _store.UpdateAsync(new VoiceSettingsUpdate(Microphone: new AudioDevice(" ", "")));

        Assert.Equal((headset, speakers), (chosen.Microphone, chosen.Speaker));
        Assert.Equal((headset, speakers), (reread.Microphone, reread.Speaker));
        Assert.Equal((headset, speakers), (untouched.Microphone, untouched.Speaker));
        Assert.Null(backToDefault.Microphone);
        Assert.Equal(speakers, backToDefault.Speaker);
    }

    [Fact]
    public async Task A_key_secure_storage_cannot_read_is_no_key()
    {
        _secrets.FailReads = true;

        Assert.Equal(new VoiceKeys(null, null), await _store.LoadKeysAsync());
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new();
        public bool FailReads { get; set; }

        public Task<string?> GetAsync(string key) => FailReads
            ? Task.FromException<string?>(new InvalidOperationException("keystore reset"))
            : Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

        public Task SetAsync(string key, string value)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public bool Remove(string key) => _values.TryRemove(key, out _);
    }
}
