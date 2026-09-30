using System.Threading.Channels;
using VoiceBot.Core.Audio;

namespace GodMode.Voice.Tests;

/// <summary>Which device voice opens, given the devices there are; and the source and sink that switch under a session.</summary>
public sealed class VoiceDeviceTests
{
    private static readonly AudioDevice Laptop = new("{0.0.1.00000000}.{laptop}", "Microphone Array (Realtek)");
    private static readonly AudioDevice Headset = new("{0.0.1.00000000}.{headset}", "Headset (WH-1000XM4 Hands-Free)");
    private static readonly AudioDevice Usb = new("{0.0.1.00000000}.{usb}", "USB Microphone");

    [Fact]
    public void Default_is_the_default_device()
    {
        var choice = VoiceDevices.Choose(null, [Laptop, Headset], Headset.Id);

        Assert.Equal(new DeviceChoice(Headset.Id, Headset.Name, Pinned: false, FellBack: false), choice);
    }

    [Fact]
    public void A_pinned_device_that_is_there_is_used_whatever_the_default()
    {
        var choice = VoiceDevices.Choose(Usb with { Id = Usb.Id.ToUpperInvariant() }, [Laptop, Headset, Usb], Headset.Id);

        Assert.Equal(new DeviceChoice(Usb.Id, Usb.Name, Pinned: true, FellBack: false), choice);
    }

    [Fact]
    public void A_pinned_device_that_is_gone_falls_back_to_the_default_and_is_used_again_when_it_comes_back()
    {
        var gone = VoiceDevices.Choose(Usb, [Laptop, Headset], Laptop.Id);
        var back = VoiceDevices.Choose(Usb, [Laptop, Headset, Usb], Laptop.Id);

        Assert.Equal(new DeviceChoice(Laptop.Id, Laptop.Name, Pinned: false, FellBack: true), gone);
        Assert.Equal(new DeviceChoice(Usb.Id, Usb.Name, Pinned: true, FellBack: false), back);
    }

    [Fact]
    public void With_no_device_of_the_kind_there_is_nothing_to_open()
    {
        Assert.Null(VoiceDevices.Choose(null, [], null).Id);
        Assert.Null(VoiceDevices.Choose(Usb, [Laptop], null).Id);
        Assert.Null(VoiceDevices.Choose(null, [Laptop], Headset.Id).Id);
    }

    [Fact]
    public async Task The_session_reads_on_from_the_new_microphone_after_a_switch_and_the_old_one_ending_does_not_end_it()
    {
        var ended = 0;
        var source = new SwitchingAudioSource(AudioFormat.Pcm16kHz, _ => Interlocked.Increment(ref ended));
        var laptop = new FakeMicrophone();
        var headset = new FakeMicrophone();

        Assert.Null(source.Use(laptop));
        laptop.Say(1);
        Assert.Equal(1, await ReadAsync(source));

        Assert.Same(laptop, source.Use(headset));
        laptop.Say(2);
        laptop.End();
        headset.Say(3);

        Assert.Equal(3, await ReadAsync(source));
        Assert.False(source.Audio.Completion.IsCompleted);
        Assert.Equal(0, ended);

        Assert.Same(headset, source.Complete());
        await source.Audio.Completion.WaitAsync(Eventually.Timeout);
    }

    [Fact]
    public async Task A_microphone_in_use_that_ends_is_reported_so_another_is_picked()
    {
        var reported = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new SwitchingAudioSource(AudioFormat.Pcm16kHz, reported.SetResult);
        var headset = new FakeMicrophone();
        source.Use(headset);

        headset.End(new InvalidOperationException("The headset went"));

        Assert.Equal("The headset went", (await reported.Task.WaitAsync(Eventually.Timeout))?.Message);
        Assert.False(source.Audio.Completion.IsCompleted);
    }

    [Fact]
    public void A_source_or_sink_in_another_format_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new SwitchingAudioSource(AudioFormat.Pcm16kHz).Use(new FakeMicrophone(AudioFormat.Pcm48kHz)));
        Assert.Throws<ArgumentException>(() => new SwitchingAudioSink(AudioFormat.Pcm16kHz).Use(new FakeSpeaker(AudioFormat.Pcm48kHz)));
    }

    [Fact]
    public async Task The_session_plays_on_the_new_speaker_after_a_switch_and_nowhere_without_one()
    {
        var sink = new SwitchingAudioSink(AudioFormat.Pcm16kHz);
        var laptop = new FakeSpeaker();
        var headset = new FakeSpeaker();

        await sink.SendAudioAsync(new byte[] { 0 }, CancellationToken.None);
        sink.Use(laptop);
        await sink.SendAudioAsync(new byte[] { 1 }, CancellationToken.None);
        Assert.Same(laptop, sink.Use(headset));
        await sink.SendAudioAsync(new byte[] { 2 }, CancellationToken.None);
        await sink.InterruptAsync(CancellationToken.None);

        Assert.Equal([1], laptop.Played);
        Assert.Equal([2], headset.Played);
        Assert.Equal((0, 1), (laptop.Interrupts, headset.Interrupts));
    }

    private static async Task<int> ReadAsync(SwitchingAudioSource source) =>
        (await source.Audio.ReadAsync().AsTask().WaitAsync(Eventually.Timeout)).Span[0];

    private sealed class FakeMicrophone(AudioFormat? format = null) : IAudioSource
    {
        private readonly Channel<ReadOnlyMemory<byte>> _audio = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        public AudioFormat Format { get; } = format ?? AudioFormat.Pcm16kHz;
        public ChannelReader<ReadOnlyMemory<byte>> Audio => _audio.Reader;

        public void Say(byte value) => _audio.Writer.TryWrite(new[] { value });
        public void End(Exception? error = null) => _audio.Writer.TryComplete(error);
    }

    private sealed class FakeSpeaker(AudioFormat? format = null) : IAudioSink
    {
        public AudioFormat Format { get; } = format ?? AudioFormat.Pcm16kHz;
        public List<byte> Played { get; } = [];
        public int Interrupts { get; private set; }

        public Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct)
        {
            Played.AddRange(audio.ToArray());
            return Task.CompletedTask;
        }

        public Task SendStatusAsync(string message, CancellationToken ct) => Task.CompletedTask;

        public Task InterruptAsync(CancellationToken ct)
        {
            Interrupts++;
            return Task.CompletedTask;
        }
    }
}
