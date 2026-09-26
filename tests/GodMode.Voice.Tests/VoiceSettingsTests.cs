using System.Collections.Concurrent;
using System.Text.Json;
using GodMode.ClientBase.Services;
using GodMode.Shared;

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
    public async Task The_defaults_are_Danish_with_English_the_fixed_models_and_no_echo_cancellation()
    {
        var view = await _store.GetViewAsync();

        Assert.Equal("da-DK+en", view.Language);
        Assert.False(view.EchoCancellation);
        Assert.Equal(new VoiceModels("claude-haiku-4-5-20251001", "claude-sonnet-5", "claude-opus-5-5"), view.Models);
        Assert.False(view.ElevenLabsKeySet);
        Assert.False(view.AnthropicKeySet);
    }

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

    [Theory]
    [InlineData("")]
    [InlineData("dansk")]
    [InlineData("xx-NOPE+en")]
    public async Task A_language_that_is_not_one_is_refused(string language) =>
        await Assert.ThrowsAsync<ArgumentException>(() => _store.UpdateAsync(new VoiceSettingsUpdate(Language: language)));

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
