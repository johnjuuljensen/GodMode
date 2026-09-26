import { useEffect, useState } from 'react';
import { useAppStore } from '../../store';
import type { PendingPermission, PermissionDetail } from '../../signalr/types';
import './QuestionPrompt.css';
import './PermissionCard.css';

interface Props {
  serverId: string;
  projectId: string;
  permission: PendingPermission;
  /** A denial carries the message typed for it, if any. A rejection is shown on the card. */
  onAnswer: (allow: boolean, message?: string) => Promise<void>;
  /** A field for the reason to deny with, where there is no input bar to type it in. Its text is the caller's, to keep. */
  denyMessage?: { value: string; onChange: (value: string) => void };
}

type DetailState = { loaded: PermissionDetail } | { failed: string } | null;

const errorText = (err: unknown) => err instanceof Error ? err.message : String(err);

/**
 * A tool call claude is holding until it is allowed or denied. Typing a reply in the input bar
 * instead denies it and tells claude what was written; without one, the deny-message field does.
 *
 * The push carries only the request's one-line summary: the card fetches everything the call would
 * run, and shows all of it, on a phone too, above an Allow that waits for it (#234). Key it by the
 * request, so each request fetches its own.
 */
export function PermissionCard({ serverId, projectId, permission, onAnswer, denyMessage }: Props) {
  const getPermissionDetail = useAppStore(s => s.getPermissionDetail);
  const [detail, setDetail] = useState<DetailState>(null);
  const [attempt, setAttempt] = useState(0);
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const message = denyMessage?.value.trim() ?? '';
  const requestId = permission.RequestId;

  useEffect(() => {
    let current = true;
    getPermissionDetail(serverId, projectId, requestId).then(
      loaded => { if (current) setDetail({ loaded }); },
      err => { if (current) setDetail({ failed: errorText(err) }); },
    );
    return () => { current = false; };
  }, [getPermissionDetail, serverId, projectId, requestId, attempt]);

  const retry = () => { setDetail(null); setAttempt(a => a + 1); };

  const answer = async (allow: boolean) => {
    setSending(true);
    setError(null);
    try {
      await onAnswer(allow, allow ? undefined : message || undefined);
    } catch (err) {
      setError(errorText(err));
    } finally {
      setSending(false);
    }
  };

  const loaded = detail && 'loaded' in detail ? detail.loaded : null;

  return (
    <div className="question-prompt permission-card">
      <div className="question-banner">
        <span className="question-pulse">!</span>
        <span className="question-label">Permission needed</span>
        <span className="question-header">{permission.ToolName}</span>
      </div>
      {loaded ? (
        <>
          <pre className="permission-detail">{loaded.Detail}</pre>
          {loaded.DetailTruncated && (
            <div className="permission-note permission-truncated">
              Only the first 16 KB is shown: the call runs with all of it. The transcript has the rest.
            </div>
          )}
        </>
      ) : (
        <>
          <div className="question-text permission-summary">{permission.Summary}</div>
          {detail && 'failed' in detail ? (
            <div className="permission-note permission-error">
              Could not load the full request: {detail.failed}{' '}
              <button className="btn btn-sm" onClick={retry}>Retry</button>
            </div>
          ) : (
            <div className="permission-note">Loading the full request…</div>
          )}
        </>
      )}
      <div className="permission-actions">
        {/* Nothing is allowed unseen: the summary is one line of what may be many */}
        <button className="btn btn-primary" onClick={() => answer(true)} disabled={sending || !loaded}
          title={loaded ? undefined : 'Allow once the full request is shown'}>Allow</button>
        <button className="btn permission-deny" onClick={() => answer(false)} disabled={sending}>
          {message ? 'Deny with message' : 'Deny'}
        </button>
        {!denyMessage && <span className="permission-hint">or type a reply to deny with a message</span>}
      </div>
      {denyMessage && (
        <input
          className="permission-deny-message"
          type="text"
          placeholder="Optional: why deny? claude reads it"
          value={denyMessage.value}
          onChange={e => denyMessage.onChange(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter' && !e.nativeEvent.isComposing && message) answer(false); }}
          disabled={sending}
        />
      )}
      {error && <div className="permission-note permission-error permission-answer-error">{error}</div>}
    </div>
  );
}
