import { useState } from 'react';
import type { PendingPermission } from '../../signalr/types';
import './QuestionPrompt.css';
import './PermissionCard.css';

interface Props {
  permission: PendingPermission;
  /** A denial carries the message typed for it, if any. */
  onAnswer: (allow: boolean, message?: string) => Promise<void>;
  /** A field for the reason to deny with, where there is no input bar to type it in. Its text is the caller's, to keep. */
  denyMessage?: { value: string; onChange: (value: string) => void };
}

/**
 * A tool call claude is holding until it is allowed or denied. Typing a reply in the input bar
 * instead denies it and tells claude what was written; without one, the deny-message field does.
 */
export function PermissionCard({ permission, onAnswer, denyMessage }: Props) {
  const [sending, setSending] = useState(false);
  const message = denyMessage?.value.trim() ?? '';

  const answer = async (allow: boolean) => {
    setSending(true);
    try { await onAnswer(allow, allow ? undefined : message || undefined); } finally { setSending(false); }
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
    </div>
  );
}
