import { useEffect, useRef, useState } from 'react';
import {
  closeMic, describeVoiceError, openMic, startVoice, stopVoice, useVoice, type VoiceStatus,
} from '../../services/voice';
import type { VoiceStateName } from '../../services/hostBridge';
import './Voice.css';

/** Whether this device shows the transcript open: closed unless the user opened it (#433). */
export const TRANSCRIPT_OPEN_KEY = 'godmode-voice-transcript-open';

const STATE_LABELS: Record<VoiceStateName, string> = {
  Off: 'Voice off',
  Starting: 'Starting…',
  Listening: 'Listening',
  Thinking: 'Thinking…',
  Speaking: 'Speaking',
  Error: 'Voice error',
};

/**
 * The voice button, its state and the transcript, where the app has voice (the shell reports voice.state Available).
 * Nothing shows where it has none. It sits in the sidebar's foot, beside Settings, never over the content (#433). While
 * voice is on, and its mic opens on demand (Windows), the Mic button beside it opens and closes the mic. It is "Mic",
 * never "mute": saying "stille" mutes the announcements.
 */
export function VoiceControl() {
  const status = useVoice();
  if (!status?.Available) return null;
  return <VoicePanel status={status} />;
}

export function VoicePanel({ status }: { status: VoiceStatus }) {
  const [busy, setBusy] = useState(false);
  const [startError, setStartError] = useState<string | null>(null);
  const [dismissed, setDismissed] = useState<string | null>(null);
  const [transcriptOpen, setTranscriptOpen] = useState(() => {
    try { return localStorage.getItem(TRANSCRIPT_OPEN_KEY) === 'true'; } catch { return false; }
  });
  const transcriptRef = useRef<HTMLOListElement>(null);
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
  const toggleTranscript = () => setTranscriptOpen(open => {
    try { localStorage.setItem(TRANSCRIPT_OPEN_KEY, String(!open)); } catch { /* not persisted */ }
    return !open;
  });

  // An error shows until it is dismissed, or the service recovers; one dismissed shows again once it has cleared
  const error = startError ?? (status.Error ? describeVoiceError(status.Error) : null);
  if (dismissed !== null && error !== dismissed) setDismissed(null);
  const dismiss = () => {
    if (startError) setStartError(null);
    else setDismissed(error);
  };
  const lines = status.Lines.slice(-8);
  const latest = lines[lines.length - 1];

  // The open transcript follows the conversation
  useEffect(() => {
    const list = transcriptRef.current;
    if (list) list.scrollTop = list.scrollHeight;
  }, [lines.length, latest?.Text, transcriptOpen]);

  const lineClass = (line: VoiceStatus['Lines'][number]) =>
    `voice-line voice-line-${line.Speaker.toLowerCase()}${line.Partial ? ' voice-line-partial' : ''}`;

  return (
    <div className={`voice-control voice-state-${status.State.toLowerCase()}`}>
      {on && transcriptOpen && (
        <ol className="voice-transcript" aria-label="Voice transcript" ref={transcriptRef}>
          {lines.length === 0 && <li className="voice-line voice-line-empty">Nothing said yet</li>}
          {lines.map((line, i) => <li key={i} className={lineClass(line)}>{line.Text}</li>)}
        </ol>
      )}
      {/* Collapsed, the latest line shows briefly, a partial included, so you see it heard you; keyed to fade again on each change */}
      {on && !transcriptOpen && latest && (
        <div key={`${lines.length}:${latest.Text}`} className={`voice-latest ${lineClass(latest)}`} title={latest.Text}>
          {latest.Text}
        </div>
      )}
      {error && error !== dismissed && (
        <div className="voice-alert" role="alert">
          <span className="voice-alert-text">{error}</span>
          <button className="voice-alert-dismiss" onClick={dismiss} title="Dismiss" aria-label="Dismiss">×</button>
        </div>
      )}
      <div className="voice-buttons">
        <button className="voice-button voice-power-button" onClick={toggle} disabled={busy || status.State === 'Starting'}
          aria-pressed={on} title={on ? 'Stop voice' : 'Start voice'}>
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <rect x="9" y="2" width="6" height="12" rx="3" /><path d="M5 10a7 7 0 0 0 14 0" /><line x1="12" y1="17" x2="12" y2="22" />
          </svg>
          <span className="voice-label">{STATE_LABELS[status.State]}</span>
        </button>
        {on && status.MicOnDemand && (
          <button className={`voice-button voice-mic-button${micOpen ? ' voice-mic-open' : ''}`} onClick={toggleMic}
            disabled={busy || status.State === 'Starting' || status.State === 'Error'}
            aria-pressed={micOpen} title={micOpen ? 'Close the mic' : 'Open the mic'}>
            <span className="voice-mic-label">Mic</span>
          </button>
        )}
        {on && (
          <button className="voice-button voice-transcript-toggle" onClick={toggleTranscript} aria-expanded={transcriptOpen}
            title={transcriptOpen ? 'Hide the transcript' : 'Show the transcript'} aria-label="Transcript">
            <svg className={`voice-chevron${transcriptOpen ? ' expanded' : ''}`} width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5">
              <polyline points="6 15 12 9 18 15" />
            </svg>
          </button>
        )}
      </div>
    </div>
  );
}
