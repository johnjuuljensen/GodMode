import { useState } from 'react';
import type { PendingPermission } from '../../signalr/types';
import './QuestionPrompt.css';
import './PermissionCard.css';

interface Props {
  permission: PendingPermission;
  /** A denial carries the message typed for it, if any. */
  onAnswer: (allow: boolean, message?: string) => Promise<void>;
  /** Shows a field for the reason to deny with, where there is no input bar to type it in. */
  withDenyMessage?: boolean;
}

/**
 * A tool call claude is holding until it is allowed or denied. Typing a reply in the input bar
 * instead denies it and tells claude what was written; without one, the deny-message field does.
 */
export function PermissionCard({ permission, onAnswer, withDenyMessage }: Props) {
  const [sending, setSending] = useState(false);
  const [denyMessage, setDenyMessage] = useState('');

  const answer = async (allow: boolean) => {
    setSending(true);
    try { await onAnswer(allow, allow ? undefined : denyMessage.trim() || undefined); } finally { setSending(false); }
  };

  return (
    <div className="question-prompt permission-card">
      <div className="question-banner">
        <span className="question-pulse">!</span>
        <span className="question-label">Permission needed</span>
        <span className="question-header">{permission.ToolName}</span>
      </div>
      <div className="question-text permission-summary">{permission.Summary}</div>
      <div className="permission-actions">
        <button className="btn btn-primary" onClick={() => answer(true)} disabled={sending}>Allow</button>
        <button className="btn permission-deny" onClick={() => answer(false)} disabled={sending}>
          {withDenyMessage && denyMessage.trim() ? 'Deny with message' : 'Deny'}
        </button>
        {!withDenyMessage && <span className="permission-hint">or type a reply to deny with a message</span>}
      </div>
      {withDenyMessage && (
        <input
          className="permission-deny-message"
          type="text"
          placeholder="Optional: why deny? claude reads it"
          value={denyMessage}
          onChange={e => setDenyMessage(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter' && !e.nativeEvent.isComposing && denyMessage.trim()) answer(false); }}
          disabled={sending}
        />
      )}
    </div>
  );
}
