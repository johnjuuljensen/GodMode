import { useState } from 'react';
import {
  describeVoiceError, startVoice, stopVoice, useVoice, type VoiceStatus,
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
 * The voice button, its state and the live transcript, in the Windows app (the shell reports voice.state Available).
 * Nothing shows elsewhere: the browser and the Android app have no voice.
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

  const toggle = async () => {
    setBusy(true);
    setStartError(null);
    try {
      await (on ? stopVoice() : startVoice());
    } catch (err) {
      setStartError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  };

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
      <button className="voice-button" onClick={toggle} disabled={busy || status.State === 'Starting'}
        aria-pressed={on} title={on ? 'Stop voice' : 'Start voice'}>
        <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
          <rect x="9" y="2" width="6" height="12" rx="3" /><path d="M5 10a7 7 0 0 0 14 0" /><line x1="12" y1="17" x2="12" y2="22" />
        </svg>
        <span className="voice-label">{STATE_LABELS[status.State]}</span>
      </button>
    </div>
  );
}
