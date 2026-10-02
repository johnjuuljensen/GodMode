import { useState } from 'react';
import {
  closeMic, describeVoiceError, openMic, startVoice, stopVoice, useVoice, type VoiceStatus,
} from '../../services/voice';
import type { VoiceStateName } from '../../services/hostBridge';
import './Voice.css';

const STATE_LABELS: Record<VoiceStateName, string> = {
  Off: 'Voice off',
  Starting: 'Starting…',
  Listening: 'Listening',
  Thinking: 'Thinking…',
  Speaking: 'Speaking',
  Error: 'Voice error',
};

/**
 * The voice button, its state and the live transcript, where the app has voice (the shell reports voice.state Available).
 * Nothing shows where it has none. While voice is on, and its mic opens on demand (Windows), the Mic button beside it
 * opens and closes the mic. It is "Mic", never "mute": saying "stille" mutes the announcements.
 */
export function VoiceControl() {
  const status = useVoice();
  if (!status?.Available) return null;
  return <VoicePanel status={status} />;
}

export function VoicePanel({ status }: { status: VoiceStatus }) {
  const [busy, setBusy] = useState(false);
  const [startError, setStartError] = useState<string | null>(null);
  const on = status.State !== 'Off';

  const micOpen = status.Mic === 'Open';
  const run = async (action: () => Promise<VoiceStatus>) => {
    setBusy(true);
    setStartError(null);
    try {
      await action();
    } catch (err) {
      setStartError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  };
  const toggle = () => run(on ? stopVoice : startVoice);
  const toggleMic = () => run(micOpen ? closeMic : openMic);

  const error = startError ?? (status.Error ? describeVoiceError(status.Error) : null);
  const lines = status.Lines.slice(-8);

  return (
    <div className={`voice-control voice-state-${status.State.toLowerCase()}`}>
      {on && lines.length > 0 && (
        <ol className="voice-transcript" aria-label="Voice transcript">
          {lines.map((line, i) => (
            <li key={i} className={`voice-line voice-line-${line.Speaker.toLowerCase()}${line.Partial ? ' voice-line-partial' : ''}`}>
              {line.Text}
            </li>
          ))}
        </ol>
      )}
      {error && <div className="voice-alert" role="alert">{error}</div>}
      <div className="voice-buttons">
        {on && status.MicOnDemand && (
          <button className={`voice-button voice-mic-button${micOpen ? ' voice-mic-open' : ''}`} onClick={toggleMic}
            disabled={busy || status.State === 'Starting' || status.State === 'Error'}
            aria-pressed={micOpen} title={micOpen ? 'Close the mic' : 'Open the mic'}>
            <span className="voice-mic-label">Mic</span>
          </button>
        )}
        <button className="voice-button voice-power-button" onClick={toggle} disabled={busy || status.State === 'Starting'}
          aria-pressed={on} title={on ? 'Stop voice' : 'Start voice'}>
          <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <rect x="9" y="2" width="6" height="12" rx="3" /><path d="M5 10a7 7 0 0 0 14 0" /><line x1="12" y1="17" x2="12" y2="22" />
          </svg>
          <span className="voice-label">{STATE_LABELS[status.State]}</span>
        </button>
      </div>
    </div>
  );
}
