/**
 * Typed request/response channel to the MAUI shell, over HybridWebView's raw messages (see hostApi.ts).
 * The C# side is GodMode.Maui/Bridge
 * (HostBridge, ShellBridge, ShellMessages.cs); keep the message types in sync.
 *
 * Envelope (JSON, PascalCase like GodMode.Shared): { Type, Id?, Payload?, Error? }.
 * A request carries an Id; the shell answers with the same Type and Id, and either a
 * Payload or an Error. A message without an Id is an event (e.g. servers.changed).
 *
 * No message from the shell carries a server's access token, nor a voice key.
 */
import type { ServerInfo } from '../signalr/types';

/** The relay's base URL and the per-launch secret it requires (as the access_token query parameter). */
export interface RelayInfo {
  BaseUrl: string;
  Secret: string;
}

export interface AddServerPayload {
  Type: string;
  DisplayName?: string | null;
  /** Local server URLs, in order of preference. */
  Urls?: string[] | null;
  Username?: string | null;
  AccessToken?: string | null;
}

interface ServerIdPayload {
  ServerId: string;
}

/** The attention item a notification tap opened: a project on a server, both IDs as the server gave them. */
export interface AttentionLinkPayload {
  ServerId: string;
  ProjectId: string;
}

/** The window a page is in (#340): the app's main window (Profile null), or a profile's own, locked to it. */
export interface WindowInfo {
  /** The profile the window is locked to, by name across every server; null in the main window. */
  Profile: string | null;
  /** Whether the app can open a profile in a window of its own (Windows). */
  CanOpenWindows: boolean;
}

interface ProfilePayload {
  Profile: string;
}

/** Whether this device makes a sound for what interrupts (#438): an important session's items. The device's own, not the session's. */
export interface AttentionSound {
  Enabled: boolean;
}

// ── Voice (the Windows app only: voice.state says whether it is Available) ──

export type VoiceStateName = 'Off' | 'Starting' | 'Listening' | 'Thinking' | 'Speaking' | 'Error';

/** VoiceBot's SessionService: the part of the session that failed. */
export type VoiceService = 'SpeechRecognition' | 'SpeechSynthesis' | 'Model' | 'Session';

/** VoiceBot's SessionErrorKind. */
export type VoiceErrorKind = 'Authentication' | 'ConnectionLost' | 'ServiceError' | 'ModelError';

export interface VoiceError {
  Service: VoiceService;
  Kind: VoiceErrorKind;
  Message: string;
}

export interface VoiceLine {
  Speaker: 'User' | 'Bot';
  Text: string;
  /** What the user is still saying: the next line from them replaces it. */
  Partial?: boolean;
}

/** Whether voice hears the user. */
export type VoiceMicState = 'Open' | 'Closed';

/** Voice in this app, and the conversation so far (the session is the app's, so a reloaded page gets it back). */
export interface VoiceStatus {
  Available: boolean;
  State: VoiceStateName;
  Lines: VoiceLine[];
  /** The last failure not yet recovered from. */
  Error?: VoiceError | null;
  /** Closed until the Mic button opens it, where it opens on demand (MicOnDemand: Windows); always Open elsewhere. */
  Mic?: VoiceMicState;
  MicOnDemand?: boolean;
}

/** A microphone or speaker: its endpoint id, and the name the platform shows for it. */
export interface AudioDevice {
  Id: string;
  Name: string;
}

/**
 * The microphones and speakers the voice settings can choose, and the default of each (its id). Not Supported where
 * voice picks its own route (Android), and the lists are empty.
 */
export interface VoiceDeviceList {
  Supported: boolean;
  Microphones: AudioDevice[];
  Speakers: AudioDevice[];
  DefaultMicrophoneId?: string | null;
  DefaultSpeakerId?: string | null;
}

/** A model per voice tier; an absent one has none. */
export interface VoiceTierModels {
  Light?: string | null;
  Medium?: string | null;
  Heavy?: string | null;
}

/** The voice settings, and whether each key is set. The shell never sends a key back. */
export interface VoiceSettingsView {
  Language: string;
  VoiceId: string;
  EchoCancellation: boolean;
  /** Null for Default, which follows the platform's default device. */
  Microphone?: AudioDevice | null;
  Speaker?: AudioDevice | null;
  /** How many seconds of silence while voice listens close the mic. */
  MicSilenceSeconds: number;
  /** Voice's short sounds: the tone as what you said is taken, and the earcons before announcements. */
  Earcons: boolean;
  /** How many hours without activity leave a session out of voice's lists, unless asked for all. */
  StaleHours: number;
  /** The model the user set for a tier, in place of VoiceBot's default; absent for a tier that has none. */
  TierModels?: VoiceTierModels | null;
  /** VoiceBot's default model of each tier, which a tier without one of its own runs on. */
  DefaultTierModels?: VoiceTierModels | null;
  ElevenLabsKeySet: boolean;
  AnthropicKeySet: boolean;
}

/**
 * What to change: an absent field stays as it is; a key that is an empty string is removed; a device whose Id is an
 * empty string is Default.
 */
export interface VoiceSettingsUpdate {
  Language?: string;
  VoiceId?: string;
  EchoCancellation?: boolean;
  Microphone?: AudioDevice;
  Speaker?: AudioDevice;
  MicSilenceSeconds?: number;
  Earcons?: boolean;
  StaleHours?: number;
  /** All three tiers: one that is empty takes VoiceBot's default. */
  TierModels?: VoiceTierModels;
  ElevenLabsKey?: string;
  AnthropicKey?: string;
}

/** Request types → [payload, response]. */
export interface BridgeRequests {
  'relay.info': [void, RelayInfo];
  'servers.list': [void, ServerInfo[]];
  'servers.add': [AddServerPayload, { Id: string }];
  'servers.remove': [ServerIdPayload, boolean];
  'servers.start': [ServerIdPayload, boolean];
  'servers.stop': [ServerIdPayload, boolean];
  'host.openDevTools': [void, boolean];
  'window.info': [void, WindowInfo];
  /** Opens the profile in its own window, or brings forward the window it has. Fails where the app has no windows (Android). */
  'window.openProfile': [ProfilePayload, boolean];
  /** The item the last notification tap opened, once; null when there is none (or it was taken). */
  'attention.take': [void, AttentionLinkPayload | null];
  'attention.sound.get': [void, AttentionSound];
  'attention.sound.set': [AttentionSound, AttentionSound];
  'voice.state': [void, VoiceStatus];
  /** Fails saying why: a missing key, no microphone, voice not available here. */
  'voice.start': [void, VoiceStatus];
  'voice.stop': [void, VoiceStatus];
  /** Pauses the music, opens the microphone, plays the rising tone. Fails where voice is off or its mic is always open. */
  'voice.mic.open': [void, VoiceStatus];
  /** The falling tone, then the microphone is let go of; the music resumes once the headset is back at full quality. */
  'voice.mic.close': [void, VoiceStatus];
  'voice.settings.get': [void, VoiceSettingsView];
  /** A running session moves to the devices it chooses now; the other settings apply from voice's next start. */
  'voice.settings.set': [VoiceSettingsUpdate, VoiceSettingsView];
  'voice.devices': [void, VoiceDeviceList];
}

/** Event types the shell sends → their payloads. attention.open: a notification was tapped, and attention.take has its item. */
export interface BridgeEvents {
  'servers.changed': void;
  'attention.open': void;
  /** What the user said; partial while they speak. */
  'voice.transcript': VoiceLine;
  /** What the bot says. */
  'voice.response': VoiceLine;
  /** A service failed; the session keeps running. */
  'voice.error': VoiceError;
  /** A service that failed works again. */
  'voice.recovered': { Service: VoiceService };
  'voice.stateChanged': VoiceStatus;
}

export type BridgeEvent = keyof BridgeEvents;

export interface BridgeMessage {
  Type: string;
  Id?: string | null;
  Payload?: unknown;
  Error?: string | null;
}

declare global {
  interface Window {
    HybridWebView?: { SendRawMessage(message: string): void };
  }
}

const REQUEST_TIMEOUT_MS = 30_000;

/** MAUI's HybridWebView script, served by the WebView itself (defines window.HybridWebView). */
const HYBRID_WEBVIEW_SCRIPT = '_framework/hybridwebview.js';

let hostReady: Promise<NonNullable<Window['HybridWebView']>> | null = null;

/** Loads the HybridWebView script once, on first use. */
function loadHost(): Promise<NonNullable<Window['HybridWebView']>> {
  hostReady ??= new Promise((resolve, reject) => {
    if (window.HybridWebView) return resolve(window.HybridWebView);
    const script = document.createElement('script');
    script.src = HYBRID_WEBVIEW_SCRIPT;
    script.onload = () => window.HybridWebView
      ? resolve(window.HybridWebView)
      : reject(new Error('HybridWebView bridge is not available'));
    script.onerror = () => reject(new Error(`Could not load ${HYBRID_WEBVIEW_SCRIPT}`));
    document.head.appendChild(script);
  });
  hostReady.catch(() => { hostReady = null; });
  return hostReady;
}

let nextId = 0;
const pending = new Map<string, { resolve: (v: unknown) => void; reject: (e: Error) => void }>();
const listeners = new Map<string, Set<(payload: unknown) => void>>();
let listening = false;

function ensureListening(): void {
  if (listening) return;
  listening = true;
  window.addEventListener('HybridWebViewMessageReceived', (e: Event) => {
    const raw = (e as CustomEvent<{ message?: unknown }>).detail?.message;
    if (typeof raw !== 'string') return;
    let msg: BridgeMessage;
    try {
      msg = JSON.parse(raw) as BridgeMessage;
    } catch {
      return; // not a bridge message
    }
    if (msg.Id) {
      const waiter = pending.get(msg.Id);
      if (!waiter) return;
      pending.delete(msg.Id);
      if (msg.Error) waiter.reject(new Error(msg.Error));
      else waiter.resolve(msg.Payload);
      return;
    }
    listeners.get(msg.Type)?.forEach(fn => fn(msg.Payload));
  });
}

/** Sends a request to the shell and resolves with its response payload. */
export async function request<K extends keyof BridgeRequests>(
  type: K,
  ...payload: BridgeRequests[K][0] extends void ? [] : [BridgeRequests[K][0]]
): Promise<BridgeRequests[K][1]> {
  ensureListening();
  const host = await loadHost();

  const id = `js-${++nextId}`;
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      pending.delete(id);
      reject(new Error(`Bridge request ${type} timed out`));
    }, REQUEST_TIMEOUT_MS);
    pending.set(id, {
      resolve: v => { clearTimeout(timer); resolve(v as BridgeRequests[K][1]); },
      reject: e => { clearTimeout(timer); reject(e); },
    });
    const message: BridgeMessage = { Type: type, Id: id, Payload: payload[0] ?? null };
    host.SendRawMessage(JSON.stringify(message));
  });
}

/** Subscribes to a shell event; the handler gets its payload. Returns the unsubscribe function. */
export function on<K extends BridgeEvent>(type: K, handler: (payload: BridgeEvents[K]) => void): () => void {
  ensureListening();
  let set = listeners.get(type);
  if (!set) listeners.set(type, set = new Set());
  const listener = handler as (payload: unknown) => void;
  set.add(listener);
  return () => { set.delete(listener); };
}
