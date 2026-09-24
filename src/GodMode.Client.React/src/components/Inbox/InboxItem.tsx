import { useState } from 'react';
import { useAppStore, type ServerAttentionItem } from '../../store';
import type { AttentionKind } from '../../signalr/types';
import { PermissionCard } from '../Project/PermissionCard';
import { ReplyInput } from '../Project/ReplyInput';

const KIND_LABELS: Record<AttentionKind, string> = {
  Permission: 'Permission',
  Question: 'Question',
  Error: 'Error',
  Review: 'Changes requested',
  Finished: 'Finished',
};

/** Kinds answered with a typed reply (ReplyAndResume). */
const REPLY_KINDS: ReadonlySet<AttentionKind> = new Set(['Question', 'Error', 'Finished']);
/** Kinds with a result to open (the pull request) and to mark seen. */
const SEEN_KINDS: ReadonlySet<AttentionKind> = new Set(['Finished', 'Review']);

interface Props {
  item: ServerAttentionItem;
  serverName: string;
  /** Now, in ms, from the inbox's clock, so every item's waiting time moves together. */
  now: number;
}

/** One project that needs the user, answerable where it is. */
export function InboxItem({ item, serverName, now }: Props) {
  const selectProject = useAppStore(s => s.selectProject);
  const replyAndResume = useAppStore(s => s.replyAndResume);
  const respondToPermission = useAppStore(s => s.respondToPermission);
  const markSeen = useAppStore(s => s.markSeen);
  const [reply, setReply] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const { serverId, ProjectId: projectId, Kind: kind } = item;
  const permission = kind === 'Permission' ? item.Permission ?? null : null;
  // A single AskUserQuestion is answered by a reply with the chosen label
  const question = kind === 'Question' && item.Question?.Questions.length === 1 ? item.Question.Questions[0] : null;
  const canReply = REPLY_KINDS.has(kind) || (kind === 'Permission' && !permission);

  /** Runs a hub call; the item leaves the inbox by AttentionChanged when it worked. */
  const run = async (call: () => Promise<void>) => {
    setBusy(true);
    setError(null);
    try {
      await call();
      return true;
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
      return false;
    } finally {
      setBusy(false);
    }
  };

  const send = async (text: string) => {
    if (!text.trim() || busy) return;
    if (await run(() => replyAndResume(serverId, projectId, text))) setReply('');
  };

  const answerPermission = async (allow: boolean, message?: string) => {
    if (!permission) return;
    await run(() => respondToPermission(serverId, projectId, permission.RequestId, { Allow: allow, Message: message ?? null }));
  };

  const meta = [item.Profile && item.Profile !== 'Default' ? item.Profile : null, serverName, `waiting ${waitingFor(item.Since, now)}`]
    .filter(Boolean).join(' · ');

  return (
    <article className={`inbox-item inbox-kind-${kind}`}>
      <button className="inbox-item-header" onClick={() => selectProject(serverId, projectId)} title="Open the project">
        <span className="inbox-item-kind">{KIND_LABELS[kind]}</span>
        <span className="inbox-item-name">{item.ProjectName}</span>
        <span className="inbox-item-meta">{meta}</span>
      </button>

      {permission ? (
        <PermissionCard permission={permission} onAnswer={answerPermission} withDenyMessage />
      ) : (
        <div className="inbox-item-text">{item.Text}</div>
      )}

      {question && question.Options.length > 0 && (
        <div className="inbox-item-options">
          {question.Options.map(o => (
            <button key={o.Label} className="btn btn-secondary" onClick={() => send(o.Label)} disabled={busy} title={o.Description ?? undefined}>
              {o.Label}
            </button>
          ))}
        </div>
      )}

      {canReply && (
        <div className="inbox-item-reply">
          <ReplyInput
            className="inbox-reply-input"
            value={reply}
            onChange={setReply}
            onSubmit={() => send(reply)}
            placeholder={kind === 'Question' ? 'Answer…' : 'Reply to continue…'}
            disabled={busy}
          />
          <button className="btn btn-primary" onClick={() => send(reply)} disabled={busy || !reply.trim()}>Send</button>
        </div>
      )}

      {SEEN_KINDS.has(kind) && (
        <div className="inbox-item-actions">
          {item.PullRequestUrl && (
            <a className="btn btn-secondary" href={item.PullRequestUrl} target="_blank" rel="noreferrer">Open PR</a>
          )}
          <button className="btn btn-secondary" onClick={() => run(() => markSeen(serverId, projectId))} disabled={busy}>
            Mark seen
          </button>
        </div>
      )}

      {error && <div className="inbox-item-error">{error}</div>}
    </article>
  );
}

/** How long since `since`: "just now", "5m", "3h", "2d". */
function waitingFor(since: string, now: number): string {
  const min = Math.floor((now - new Date(since).getTime()) / 60000);
  if (min < 1) return 'just now';
  if (min < 60) return `${min}m`;
  const hr = Math.floor(min / 60);
  return hr < 24 ? `${hr}h` : `${Math.floor(hr / 24)}d`;
}
