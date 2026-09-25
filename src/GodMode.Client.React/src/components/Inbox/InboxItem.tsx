import { useState } from 'react';
import { useAppStore, projectKey, type ServerAttentionItem } from '../../store';
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
  /** A tapped notification opened this item: it is marked (and the inbox scrolls to it). */
  focused?: boolean;
}

/** One project that needs the user, answerable where it is. */
export function InboxItem({ item, serverName, now, focused = false }: Props) {
  const selectProject = useAppStore(s => s.selectProject);
  const replyAndResume = useAppStore(s => s.replyAndResume);
  const respondToPermission = useAppStore(s => s.respondToPermission);
  const markSeen = useAppStore(s => s.markSeen);
  const { serverId, ProjectId: projectId, Kind: kind } = item;
  // Held in the store, so a remount of this item (a layout change, the pane collapsed) keeps it
  const draft = useAppStore(s => s.inboxDrafts[projectKey(serverId, projectId)]);
  const setInboxDraft = useAppStore(s => s.setInboxDraft);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // The project needs the user anew (another kind, or the same kind again): nothing of the last one carries over (#218)
  const need = `${kind} ${item.Since}`;
  const [needSeen, setNeedSeen] = useState(need);
  if (need !== needSeen) {
    setNeedSeen(need);
    setBusy(false);
    setError(null);
  }

  const permission = kind === 'Permission' ? item.Permission ?? null : null;
  const reply = draft?.reply ?? '';
  const setReply = (text: string) => setInboxDraft(serverId, projectId, { reply: text });
  // A reason typed for a request answered elsewhere is not the next request's (#218)
  const denyMessage = permission && draft?.deny?.requestId === permission.RequestId ? draft.deny.message : '';
  const setDenyMessage = (text: string) => {
    if (permission) setInboxDraft(serverId, projectId, { deny: text ? { requestId: permission.RequestId, message: text } : null });
  };
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
    if (await run(() => respondToPermission(serverId, projectId, permission.RequestId, { Allow: allow, Message: message ?? null }))) setDenyMessage('');
  };

  const open = (e: React.MouseEvent<HTMLButtonElement>) => {
    // A clicked button keeps the focus (Chromium on Windows, WebView2), and beside the open project this one
    // stays: let go of it, so its question's keys, which are the prompt's or the page's (#240), reach it (#218)
    e.currentTarget.blur();
    selectProject(serverId, projectId);
  };

  const meta = [item.Profile && item.Profile !== 'Default' ? item.Profile : null, serverName, `waiting ${waitingFor(item.Since, now)}`]
    .filter(Boolean).join(' · ');

  return (
    <article className={`inbox-item inbox-kind-${kind}${focused ? ' inbox-item-focused' : ''}`}>
      <button className="inbox-item-header" onClick={open} title="Open the project">
        <span className="inbox-item-kind">{KIND_LABELS[kind]}</span>
        <span className="inbox-item-name">{item.ProjectName}</span>
        <span className="inbox-item-meta">{meta}</span>
      </button>

      {permission ? (
        // One card per request: the next one does not start out sending, as the last one was (#218)
        <PermissionCard key={permission.RequestId} permission={permission} onAnswer={answerPermission} denyMessage={{ value: denyMessage, onChange: setDenyMessage }} />
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

/** How long since `since`: "<1m", "5m", "3h", "2d". */
function waitingFor(since: string, now: number): string {
  const min = Math.floor((now - new Date(since).getTime()) / 60000);
  if (min < 1) return '<1m';
  if (min < 60) return `${min}m`;
  const hr = Math.floor(min / 60);
  return hr < 24 ? `${hr}h` : `${Math.floor(hr / 24)}d`;
}
