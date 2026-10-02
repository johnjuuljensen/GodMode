using System.Collections.Concurrent;
using GodMode.Maui;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceBot.Core.Audio;

namespace GodMode.Voice.Tests;

/// <summary>
/// The headset's call (GodMode.Maui's <c>HeadsetCall</c>, compiled in from its source, issues #423 and #442) under a
/// real <see cref="VoiceMic"/>: the headset's button ends the call, and the mic closes, even while the call is becoming
/// active. Over a fake phone line.
/// </summary>
public sealed class HeadsetCallTests : IDisposable
{
    private readonly ConcurrentQueue<string> _log = new();
    // Never moved: no silence closes the mic
    private readonly ManualTime _time = new();
    private readonly FakeLine _line;
    private readonly HeadsetCall _call;
    private readonly FakeMicSwitch _switch;
    private readonly VoiceMic _mic;

    public HeadsetCallTests()
    {
        _line = new FakeLine(_log);
        _call = new HeadsetCall(_line, NullLogger.Instance);
        _switch = new FakeMicSwitch(_log);
        _mic = new VoiceMic(_switch, new SilentSink(), null, new VoiceMicOptions { ToneWait = TimeSpan.Zero }, NullLogger.Instance,
            _time, _call);
    }

    public void Dispose()
    {
        _mic.Dispose();
        _call.Dispose();
    }

    [Fact]
    public async Task The_headsets_button_while_the_call_becomes_active_ends_it_and_closes_the_mic()
    {
        _line.WhileActivating = call => call.RequestEnd();

        await _mic.OpenAsync();

        await Eventually.UntilAsync(() => _switch.Closes == 1, () => "the mic did not close");
        Assert.Equal(VoiceMicState.Closed, _mic.State);
        // Ended as the button asked, and again once the activation is through, which may have landed after the first
        Assert.Equal(["open", "call activating", "call ended", "call ended", "close"], _log);
    }

    /// <summary>A close while Windows makes the call active: it is ended again after, so Windows holds no call nobody ends.</summary>
    [Fact]
    public async Task A_mic_closed_while_the_call_becomes_active_ends_the_call_once_it_is()
    {
        _line.WhileActivating = _ => _call.End();

        await _call.StartAsync();

        Assert.Equal(["call activating", "call ended", "call ended"], _log);
        // Nothing is left to end
        _call.End();
        Assert.Equal(3, _log.Count);
    }

    [Fact]
    public async Task The_headsets_button_once_the_call_is_active_ends_it_and_closes_the_mic()
    {
        await _mic.OpenAsync();
        Assert.Equal(["open", "call activating", "call active"], _log);

        _line.Last!.RequestEnd();

        await Eventually.UntilAsync(() => _switch.Closes == 1, () => "the mic did not close");
        Assert.Equal(["open", "call activating", "call active", "call ended", "close"], _log);
    }

    [Fact]
    public async Task A_mic_closed_while_the_call_is_requested_ends_the_call_it_makes()
    {
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _line.Requested = requested.Task;
        var starting = _call.StartAsync();

        _call.End();
        requested.SetResult();
        await starting;

        Assert.Equal(["call ended"], _log);
    }

    private sealed class FakeLine(ConcurrentQueue<string> log) : IPhoneLine
    {
        /// <summary>What happens while the call becomes active, before <c>NotifyActive</c> returns.</summary>
        public Action<FakeCall>? WhileActivating { get; set; }
        public Task Requested { get; set; } = Task.CompletedTask;
        public FakeCall? Last { get; private set; }

        public async Task<IPhoneCall?> RequestAsync()
        {
            await Requested;
            return Last = new FakeCall(log, WhileActivating);
        }
    }

    private sealed class FakeCall(ConcurrentQueue<string> log, Action<FakeCall>? whileActivating) : IPhoneCall
    {
        public event Action? EndRequested;

        /// <summary>As Windows may: the headset's button, or a close, before <c>NotifyCallActive</c> returns.</summary>
        public void NotifyActive()
        {
            log.Enqueue("call activating");
            if (whileActivating is not null)
            {
                whileActivating(this);
                return;
            }
            log.Enqueue("call active");
        }

        public void NotifyEnded() => log.Enqueue("call ended");

        public void RequestEnd() => EndRequested?.Invoke();
    }

    private sealed class FakeMicSwitch(ConcurrentQueue<string> log) : IMicSwitch
    {
        private int _closes;

        public int Closes => Volatile.Read(ref _closes);

        public void OpenMic() => log.Enqueue("open");

        public void CloseMic()
        {
            Interlocked.Increment(ref _closes);
            log.Enqueue("close");
        }
    }

    private sealed class SilentSink : IAudioSink
    {
        public AudioFormat Format => AudioFormat.Pcm16kHz;
        public Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct) => Task.CompletedTask;
        public Task SendStatusAsync(string message, CancellationToken ct) => Task.CompletedTask;
        public Task InterruptAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
