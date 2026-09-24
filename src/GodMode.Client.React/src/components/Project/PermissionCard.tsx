import { useState } from 'react';
import type { PendingPermission } from '../../signalr/types';
import './QuestionPrompt.css';
import './PermissionCard.css';

interface Props {
  permission: PendingPermission;
  onAnswer: (allow: boolean) => Promise<void>;
}

/**
 * A tool call claude is holding until it is allowed or denied. Typing a reply in the input bar
 * instead denies it and tells claude what was written.
 */
export function PermissionCard({ permission, onAnswer }: Props) {
  const [sending, setSending] = useState(false);

  const answer = async (allow: boolean) => {
    setSending(true);
    try { await onAnswer(allow); } finally { setSending(false); }
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
        <button className="btn permission-deny" onClick={() => answer(false)} disabled={sending}>Deny</button>
        <span className="permission-hint">or type a reply to deny with a message</span>
      </div>
    </div>
  );
}
