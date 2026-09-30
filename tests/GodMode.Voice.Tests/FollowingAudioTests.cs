using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceBot.Core.Audio;

namespace GodMode.Voice.Tests;

/// <summary>
/// Voice's devices as they come and go: Default follows the default device, a chosen one is used while it is there,
/// and the session keeps reading and playing through every switch. Over a fake device list, so no audio hardware.
/// </summary>
public sealed class FollowingAudioTests : IDisposable
{
    private static readonly AudioDevice LaptopMic = new("{0.0.1}.{laptop}", "Microphone Array (Realtek)");
    private static readonly AudioDevice HeadsetMic = new("{0.0.1}.{headset}", "Headset (Shokz)");
    private static readonly AudioDevice UsbMic = new("{0.0.1}.{usb}", "USB Microphone");
    private static readonly AudioDevice LaptopSpeakers = new("{0.0.0}.{laptop}", "Speakers (Realtek)");
    private static readonly AudioDevice HeadsetSpeaker = new("{0.0.0}.{headset}", "Headphones (Shokz)");

    private readonly FakeDevices _devices = new();
    private FollowingAudio? _audio;

    public void Dispose() => _audio?.Dispose();

    private FollowingAudio Open(bool echoCancellation = false, AudioDevice? microphone = null, AudioDevice? speaker = null)
    {
        _audio = new FollowingAudio(_devices, echoCancellation, microphone, speaker, NullLogger.Instance, TimeSpan.FromMilliseconds(10));
        _audio.Start();
        return _audio;
    }

    [Fact]
    public async Task Default_follows_a_headset_turned_on_and_off()
    {
        _devices.Set([LaptopMic], [LaptopSpeakers], LaptopMic, LaptopSpeakers);
        var audio = Open();
        Assert.Equal((LaptopMic.Id, LaptopSpeakers.Id), audio.OpenIds);

        _devices.Set([LaptopMic, HeadsetMic], [LaptopSpeakers, HeadsetSpeaker], HeadsetMic, HeadsetSpeaker);
        await UntilOpenAsync(audio, HeadsetMic, HeadsetSpeaker);
        _devices.Microphone(HeadsetMic).Say(7);
        await audio.Sink.SendAudioAsync(new byte[] { 9 }, CancellationToken.None);

        Assert.Equal(7, await ReadAsync(audio));
        Assert.Equal([9], _devices.Speaker(HeadsetSpeaker).Played);
        Assert.True(_devices.Microphone(LaptopMic).Disposed);
        Assert.True(_devices.Speaker(LaptopSpeakers).Disposed);
        Assert.True(_devices.Microphone(HeadsetMic).Started);

        _devices.Set([LaptopMic], [LaptopSpeakers], LaptopMic, LaptopSpeakers);
        await UntilOpenAsync(audio, LaptopMic, LaptopSpeakers);
        _devices.Microphone(LaptopMic).Say(8);
        Assert.Equal(8, await ReadAsync(audio));
        Assert.False(audio.Source.Audio.Completion.IsCompleted);
    }

    [Fact]
    public async Task A_chosen_device_that_goes_falls_back_to_the_default_and_is_used_again_when_it_is_back()
    {
        _devices.Set([LaptopMic, UsbMic], [LaptopSpeakers], LaptopMic, LaptopSpeakers);
        var audio = Open(microphone: UsbMic);
        Assert.Equal(UsbMic.Id, audio.OpenIds.Microphone);

        _devices.Set([LaptopMic], [LaptopSpeakers], LaptopMic, LaptopSpeakers);
        await UntilOpenAsync(audio, LaptopMic, LaptopSpeakers);

        _devices.Set([LaptopMic, UsbMic], [LaptopSpeakers], LaptopMic, LaptopSpeakers);
        await UntilOpenAsync(audio, UsbMic, LaptopSpeakers);
    }

    [Fact]
    public void Devices_the_settings_choose_are_used_at_once_and_Default_follows_again_after()
    {
        _devices.Set([LaptopMic, HeadsetMic], [LaptopSpeakers, HeadsetSpeaker], LaptopMic, LaptopSpeakers);
        var audio = Open();

        audio.UseDevices(HeadsetMic, HeadsetSpeaker);
        Assert.Equal((HeadsetMic.Id, HeadsetSpeaker.Id), audio.OpenIds);

        audio.UseDevices(null, null);
        Assert.Equal((LaptopMic.Id, LaptopSpeakers.Id), audio.OpenIds);
        Assert.Equal(3, _devices.Opened.OfType<FakeMicrophone>().Count());
    }

    [Fact]
    public void Echo_cancellation_runs_with_Default_for_both_and_not_with_a_chosen_device()
    {
        _devices.Set([LaptopMic, UsbMic], [LaptopSpeakers], LaptopMic, LaptopSpeakers);

        Open(echoCancellation: true).Dispose();
        Open(echoCancellation: true, microphone: UsbMic).Dispose();

        Assert.Equal([(LaptopMic.Id, true), (UsbMic.Id, false)],
            _devices.Opened.OfType<FakeMicrophone>().Select(m => (m.Id, m.EchoCancelled)));
        Assert.Equal([true, false], _devices.Listed);
    }

    [Fact]
    public async Task A_microphone_that_stops_by_itself_is_opened_again()
    {
        _devices.Set([LaptopMic], [LaptopSpeakers], LaptopMic, LaptopSpeakers);
        var audio = Open();
        var first = _devices.Microphone(LaptopMic);

        first.End(new InvalidOperationException("The microphone stopped delivering audio"));

        await Eventually.UntilAsync(() => _devices.Microphone(LaptopMic) != first, () => "the microphone was not opened again");
        _devices.Microphone(LaptopMic).Say(5);
        Assert.Equal(5, await ReadAsync(audio));
    }

    [Fact]
    public void With_no_device_voice_hears_and_plays_nothing_but_keeps_going()
    {
        _devices.Set([], [], null, null);
        var audio = Open();

        Assert.Equal((null, null), audio.OpenIds);
        Assert.False(audio.Source.Audio.Completion.IsCompleted);
        Assert.True(audio.Sink.SendAudioAsync(new byte[] { 1 }, CancellationToken.None).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Stopping_lets_go_of_the_devices_and_ends_what_the_session_reads()
    {
        _devices.Set([LaptopMic], [LaptopSpeakers], LaptopMic, LaptopSpeakers);
        var audio = Open();

        audio.Dispose();

        Assert.True(_devices.Microphone(LaptopMic).Disposed);
        Assert.True(_devices.Speaker(LaptopSpeakers).Disposed);
        Assert.False(_devices.Watching);
        await audio.Source.Audio.Completion.WaitAsync(Eventually.Timeout);
    }

    private static Task UntilOpenAsync(FollowingAudio audio, AudioDevice microphone, AudioDevice speaker) =>
        Eventually.UntilAsync(() => audio.OpenIds == (microphone.Id, speaker.Id),
            () => $"open on {audio.OpenIds}, not ({microphone.Id}, {speaker.Id})");

    private static async Task<int> ReadAsync(FollowingAudio audio) =>
        (await audio.Source.Audio.ReadAsync().AsTask().WaitAsync(Eventually.Timeout)).Span[0];

    private sealed class FakeDevices : IAudioDevices
    {
        private VoiceDeviceList _list = VoiceDeviceList.None;
        private Action? _changed;

        public ConcurrentQueue<IFake> Opened { get; } = new();
        public ConcurrentQueue<bool> Listed { get; } = new();
        public bool Watching => _changed is not null;

        /// <summary>The devices there are now; the watcher hears of it, as Windows' notification client would.</summary>
        public void Set(AudioDevice[] microphones, AudioDevice[] speakers, AudioDevice? defaultMicrophone, AudioDevice? defaultSpeaker)
        {
            _list = new VoiceDeviceList(true, microphones, speakers, defaultMicrophone?.Id, defaultSpeaker?.Id);
            _changed?.Invoke();
        }

        public FakeMicrophone Microphone(AudioDevice device) => Opened.OfType<FakeMicrophone>().Last(m => m.Id == device.Id);
        public FakeSpeaker Speaker(AudioDevice device) => Opened.OfType<FakeSpeaker>().Last(s => s.Id == device.Id);

        public VoiceDeviceList List(bool echoCancelled)
        {
            Listed.Enqueue(echoCancelled);
            return _list;
        }

        public IDisposable Watch(Action changed)
        {
            _changed = changed;
            return new Unwatch(() => _changed = null);
        }

        public IMicrophone OpenMicrophone(AudioDevice device, bool echoCancelled) => Keep(new FakeMicrophone(device.Id, echoCancelled));
        public ISpeaker OpenSpeaker(AudioDevice device) => Keep(new FakeSpeaker(device.Id));

        private T Keep<T>(T device) where T : IFake
        {
            Opened.Enqueue(device);
            return device;
        }

        private sealed class Unwatch(Action unwatch) : IDisposable
        {
            public void Dispose() => unwatch();
        }
    }

    private interface IFake
    {
        string Id { get; }
    }

    private sealed class FakeMicrophone(string id, bool echoCancelled) : IMicrophone, IFake
    {
        private readonly Channel<ReadOnlyMemory<byte>> _audio = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        public string Id => id;
        public bool EchoCancelled => echoCancelled;
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public AudioFormat Format => AudioFormat.Pcm16kHz;
        public ChannelReader<ReadOnlyMemory<byte>> Audio => _audio.Reader;
        public string Description => echoCancelled ? "echo-cancelled" : "plain";

        public void Say(byte value) => _audio.Writer.TryWrite(new[] { value });
        public void End(Exception error) => _audio.Writer.TryComplete(error);
        public void Start() => Started = true;

        public void Dispose()
        {
            Disposed = true;
            _audio.Writer.TryComplete();
        }
    }

    private sealed class FakeSpeaker(string id) : ISpeaker, IFake
    {
        public string Id => id;
        public bool Disposed { get; private set; }
        public List<byte> Played { get; } = [];
        public AudioFormat Format => AudioFormat.Pcm16kHz;

        public Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct)
        {
            Played.AddRange(audio.ToArray());
            return Task.CompletedTask;
        }

        public Task SendStatusAsync(string message, CancellationToken ct) => Task.CompletedTask;
        public Task InterruptAsync(CancellationToken ct) => Task.CompletedTask;
        public void Dispose() => Disposed = true;
    }
}
