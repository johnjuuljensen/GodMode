/**
 * Voice, in the Windows app: the shell runs the session (GodMode.Maui/Voice/VoiceHost.cs) and this page shows it.
 * The session is the app's, not the page's: a reload finds it running and gets the conversation so far.
 * Everywhere else (the browser, the Android app) voice is not available and nothing here reaches the shell.
 */
import { useEffect, useState } from 'react';
import { isMaui } from './hostApi';
import * as bridge from './hostBridge';
import type {
  VoiceError, VoiceLine, VoiceService, VoiceSettingsUpdate, VoiceSettingsView, VoiceStatus,
} from './hostBridge';

export type { VoiceError, VoiceLine, VoiceSettingsUpdate, VoiceSettingsView, VoiceStatus } from './hostBridge';

const UNAVAILABLE: VoiceStatus = { Available: false, State: 'Off', Lines: [] };

export const getVoiceStatus = (): Promise<VoiceStatus> =>
  isMaui ? bridge.request('voice.state') : Promise.resolve(UNAVAILABLE);
export const startVoice = (): Promise<VoiceStatus> => bridge.request('voice.start');
export const stopVoice = (): Promise<VoiceStatus> => bridge.request('voice.stop');
export const getVoiceSettings = (): Promise<VoiceSettingsView> => bridge.request('voice.settings.get');
export const setVoiceSettings = (update: VoiceSettingsUpdate): Promise<VoiceSettingsView> =>
  bridge.request('voice.settings.set', update);

const SERVICE_NAMES: Record<VoiceService, string> = {
  SpeechRecognition: 'ElevenLabs (speech recognition)',
  SpeechSynthesis: 'ElevenLabs (speech)',
  Model: 'Claude',
  Session: 'The voice session',
};

/** What failed and what to do about it, for the user. */
export function describeVoiceError(error: VoiceError): string {
  const service = error.Service === 'Model' ? 'Claude' : error.Service === 'Session' ? 'The voice session' : 'ElevenLabs';
  switch (error.Kind) {
    case 'Authentication':
      return `${service} refused the key: check it in the voice settings.`;
    case 'ConnectionLost':
      return `${SERVICE_NAMES[error.Service]}: connection lost, retrying.`;
    case 'ModelError':
      return `Claude could not answer (${error.Message}). Try again.`;
    default:
      return `${SERVICE_NAMES[error.Service]} failed: ${error.Message}`;
  }
}

/** The conversation with a transcript line added: a partial from the user is replaced by what follows it. */
export function withLine(lines: VoiceLine[], line: VoiceLine): VoiceLine[] {
  const last = lines[lines.length - 1];
  const kept = last?.Speaker === 'User' && last.Partial ? lines.slice(0, -1) : lines;
  return [...kept, line];
}

/** Voice as the shell reports it, kept current by its events. Null until the shell answers (never in a browser). */
export function useVoice(): VoiceStatus | null {
  const [status, setStatus] = useState<VoiceStatus | null>(null);

  useEffect(() => {
    if (!isMaui) return;
    let live = true;
    const update = (change: (s: VoiceStatus) => VoiceStatus) => setStatus(s => (s ? change(s) : s));
    const unsubscribe = [
      bridge.on('voice.stateChanged', next => setStatus(next)),
      bridge.on('voice.transcript', line => update(s => ({ ...s, Lines: withLine(s.Lines, line) }))),
      bridge.on('voice.response', line => update(s => ({ ...s, Lines: withLine(s.Lines, line) }))),
      bridge.on('voice.error', error => update(s => ({ ...s, Error: error }))),
      bridge.on('voice.recovered', ({ Service }) => update(s => (s.Error?.Service === Service ? { ...s, Error: null } : s))),
    ];
    getVoiceStatus()
      .then(s => { if (live) setStatus(s); })
      .catch(err => console.error('[voice] voice.state failed:', err));
    return () => {
      live = false;
      unsubscribe.forEach(u => u());
    };
  }, []);

  return status;
}
