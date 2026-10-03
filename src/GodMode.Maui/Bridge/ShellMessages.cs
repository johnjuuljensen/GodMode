using GodMode.Voice;
using VoiceBot.Core.Pipeline;

namespace GodMode.Maui.Bridge;

/// <summary>
/// Message types the React app (services/hostBridge.ts) exchanges with the shell. Keep the two in sync.
/// No response carries a server's access token: it goes from React to the shell in servers.add and never back.
/// Nor does any carry a voice key: they go from React to the shell in voice.settings.set and never back.
/// </summary>
public static class ShellMessageTypes
{
    /// <summary>Request → <see cref="RelayInfo"/>.</summary>
    public const string RelayInfo = "relay.info";

    /// <summary>Request → ServerInfo[].</summary>
    public const string ServersList = "servers.list";

    /// <summary>Request <see cref="AddServerPayload"/> → <see cref="AddServerResult"/>.</summary>
    public const string ServersAdd = "servers.add";

    /// <summary>Request <see cref="ServerIdPayload"/>; removes the registration serving the server.</summary>
    public const string ServersRemove = "servers.remove";

    /// <summary>Request <see cref="ServerIdPayload"/>.</summary>
    public const string ServersStart = "servers.start";

    /// <summary>Request <see cref="ServerIdPayload"/>.</summary>
    public const string ServersStop = "servers.stop";

    /// <summary>Request; opens the WebView's developer tools where the platform has them (Windows).</summary>
    public const string OpenDevTools = "host.openDevTools";

    /// <summary>Request → <see cref="Bridge.WindowInfo"/>: the window the page is in (#340).</summary>
    public const string WindowInfo = "window.info";

    /// <summary>
    /// Request <see cref="ProfilePayload"/>: opens the profile in its own window, or brings forward the window it has.
    /// Fails where the app opens no windows (<see cref="Bridge.WindowInfo.CanOpenWindows"/>).
    /// </summary>
    public const string WindowOpenProfile = "window.openProfile";

    /// <summary>Event from the shell, to every window's page: the server list or a server's state changed.</summary>
    public const string ServersChanged = "servers.changed";

    /// <summary>Request → <see cref="AttentionLinkPayload"/>, or null: the item a notification tap opened, once.</summary>
    public const string AttentionTake = "attention.take";

    /// <summary>Event from the shell: a notification was tapped; attention.take has its item.</summary>
    public const string AttentionOpen = "attention.open";

    /// <summary>Request → <see cref="AttentionSoundPayload"/>: whether this device makes a sound for what interrupts.</summary>
    public const string AttentionSoundGet = "attention.sound.get";

    /// <summary>Request <see cref="AttentionSoundPayload"/> → <see cref="AttentionSoundPayload"/>: sets it, for this device.</summary>
    public const string AttentionSoundSet = "attention.sound.set";

    /// <summary>Request → <see cref="VoiceStatus"/>: whether voice is available here, its state, and the conversation so far.</summary>
    public const string VoiceState = "voice.state";

    /// <summary>Request → <see cref="VoiceStatus"/>, once the session listens. Fails saying why (a missing key, no microphone).</summary>
    public const string VoiceStart = "voice.start";

    /// <summary>Request → <see cref="VoiceStatus"/>, once the session has stopped.</summary>
    public const string VoiceStop = "voice.stop";

    /// <summary>
    /// Request → <see cref="VoiceStatus"/>, once the mic is open: the music paused, the microphone open, the rising tone
    /// played. Fails where voice is off, or its mic does not open on demand (<see cref="VoiceStatus.MicOnDemand"/>).
    /// </summary>
    public const string VoiceMicOpen = "voice.mic.open";

    /// <summary>Request → <see cref="VoiceStatus"/>, once the mic has closed: the falling tone, the microphone let go of.</summary>
    public const string VoiceMicClose = "voice.mic.close";

    /// <summary>Request → <see cref="VoiceSettingsView"/>: the settings, and whether each key is set; never a key.</summary>
    public const string VoiceSettingsGet = "voice.settings.get";

    /// <summary>Request <see cref="VoiceSettingsUpdate"/> → <see cref="VoiceSettingsView"/>. Keys go into secure storage.</summary>
    public const string VoiceSettingsSet = "voice.settings.set";

    /// <summary>
    /// Request → <see cref="VoiceDeviceList"/>: the microphones and speakers the voice settings can choose, and the
    /// defaults. Not <see cref="VoiceDeviceList.Supported"/> where voice picks its own route (Android).
    /// </summary>
    public const string VoiceDevices = "voice.devices";

    /// <summary>Event <see cref="VoiceLine"/> (<see cref="VoiceSpeaker.User"/>): what the user said, partial while they speak.</summary>
    public const string VoiceTranscript = "voice.transcript";

    /// <summary>Event <see cref="VoiceLine"/> (<see cref="VoiceSpeaker.Bot"/>): what the bot says.</summary>
    public const string VoiceResponse = "voice.response";

    /// <summary>Event <see cref="VoiceErrorPayload"/>: a service failed. The session keeps running.</summary>
    public const string VoiceError = "voice.error";

    /// <summary>Event <see cref="VoiceServicePayload"/>: a service that failed works again.</summary>
    public const string VoiceRecovered = "voice.recovered";

    /// <summary>Event <see cref="VoiceStatus"/>: the state changed, or the mic opened or closed.</summary>
    public const string VoiceStateChanged = "voice.stateChanged";
}

public enum VoiceSpeaker { User, Bot }

/// <summary>One line of the conversation.</summary>
public sealed record VoiceLine(VoiceSpeaker Speaker, string Text, bool Partial = false);

/// <summary>
/// Voice in this app: <paramref name="Available"/> is false where the platform has no voice (Windows and Android have it), and
/// <paramref name="Lines"/> is the conversation so far, so a page loaded again shows it. <paramref name="Mic"/> is whether
/// voice hears the user; it opens on demand where <paramref name="MicOnDemand"/> (Windows), and is always open elsewhere.
/// </summary>
public sealed record VoiceStatus(bool Available, VoiceState State, IReadOnlyList<VoiceLine> Lines, VoiceErrorPayload? Error = null,
    VoiceMicState Mic = VoiceMicState.Open, bool MicOnDemand = false);

/// <summary>A failure: which service (<see cref="SessionService"/>), what kind (<see cref="SessionErrorKind"/>), and its message.</summary>
public sealed record VoiceErrorPayload(SessionService Service, SessionErrorKind Kind, string Message);

public sealed record VoiceServicePayload(SessionService Service);

/// <summary>The relay's base URL and the per-launch secret it requires (as the access_token query parameter).</summary>
public sealed record RelayInfo(string BaseUrl, string Secret);

/// <summary>A server to register. A local server has one or more URLs, in order of preference.</summary>
public sealed record AddServerPayload(
    string Type,
    string? DisplayName,
    IReadOnlyList<string>? Urls,
    string? Username,
    string? AccessToken);

public sealed record AddServerResult(string Id);

public sealed record ServerIdPayload(string ServerId);

/// <summary>
/// The window a page is in: the app's main window (<paramref name="Profile"/> null), unlocked, or a profile's own,
/// locked to that profile by name across every server. <paramref name="CanOpenWindows"/>: the app opens profile
/// windows here (Windows).
/// </summary>
public sealed record WindowInfo(string? Profile, bool CanOpenWindows);

public sealed record ProfilePayload(string Profile);

/// <summary>Whether this device makes a sound for what interrupts (<see cref="AttentionSound"/>).</summary>
public sealed record AttentionSoundPayload(bool Enabled);

/// <summary>An attention item to open in the inbox: a project on a server, both IDs as the server gave them.</summary>
public sealed record AttentionLinkPayload(string ServerId, string ProjectId);
