import { useState } from 'react';
import { useAppStore, projectKey, type ServerAttentionItem } from '../../store';
import type { AttentionKind, QuestionItem } from '../../signalr/types';
import { PermissionCard } from '../Project/PermissionCard';
import { ReplyInput } from '../Project/ReplyInput';
import { hubErrorMessage } from '../../signalr/hubError';
import { deleteSession } from '../../deleteSession';
import { ImportanceMark } from '../Importance/Importance';

const KIND_LABELS: Record<AttentionKind, string> = {
  Permission: 'Permission',
  Question: 'Question',
  Error: 'Error',
  Escalation: 'Decision',
  Review: 'Changes requested',
  Finished: 'Finished',
};

/**
 * The item's label: what the session said its turn's end is (#467). A finished turn is Done only when it said so, and
 * Idle otherwise, which says nothing of the work; a question it said it is blocked on, or needs the user for, says that.
 */
const kindLabel = ({ Kind: kind, Outcome: outcome }: ServerAttentionItem): string =>
  kind === 'Finished' ? (outcome === 'done' ? 'Done' : 'Idle')
    : kind === 'Question' && outcome === 'blocked' ? 'Blocked'
    : kind === 'Question' && outcome === 'needs-you' ? 'Needs you'
    : KIND_LABELS[kind];

/** Kinds answered with a typed reply (ReplyAndResume). */
const REPLY_KINDS: ReadonlySet<AttentionKind> = new Set(['Question', 'Error', 'Escalation', 'Finished']);
/** Kinds to mark seen: a Question only in plain text, as a pending AskUserQuestion is answered (#426). */
const SEEN_KINDS: ReadonlySet<AttentionKind> = new Set(['Finished', 'Review', 'Question', 'Error', 'Escalation']);

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
  const answerQuestion = useAppStore(s => s.answerQuestion);
  const { serverId, ProjectId: projectId, Kind: kind } = item;
  // Held in the store, so a remount of this item (a layout change, the pane collapsed) keeps it
  const draft = useAppStore(s => s.inboxDrafts[projectKey(serverId, projectId)]);
  const setInboxDraft = useAppStore(s => s.setInboxDraft);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Held from before its server was lost: still answerable once it is back, and a reply meanwhile says it is offline (#221)
  const offline = useAppStore(s => s.getConnection(serverId)?.connectionState !== 'connected');

  // The project needs the user anew (another kind, or the same kind again): nothing of the last one carries over (#218)
  const need = `${kind} ${item.Since}`;
  const [needSeen, setNeedSeen] = useState(need);
  // A pending AskUserQuestion's choices so far, by question text, for a request answered all at once
  const [chosen, setChosen] = useState<Record<string, readonly string[]>>({});
  if (need !== needSeen) {
    setNeedSeen(need);
    setBusy(false);
    setError(null);
    setChosen({});
  }

  const permission = kind === 'Permission' ? item.Permission ?? null : null;
  const reply = draft?.reply ?? '';
  const setReply = (text: string) => setInboxDraft(serverId, projectId, { reply: text });
  // A reason typed for a request answered elsewhere is not the next request's (#218)
  const denyMessage = permission && draft?.deny?.requestId === permission.RequestId ? draft.deny.message : '';
  const setDenyMessage = (text: string) => {
    if (permission) setInboxDraft(serverId, projectId, { deny: text ? { requestId: permission.RequestId, message: text } : null });
  };
  // A pending AskUserQuestion shows every question in full (#454). A single single-select one is answered by a
  // reply with the chosen label; several, or a multi-select, by one answer per question, sent together
  const pending = kind === 'Question' ? item.Question ?? null : null;
  const answersAtOnce = pending !== null && (pending.Questions.length > 1 || pending.Questions.some(q => q.MultiSelect));
  // A create that failed before its launch has no session to reply to: its delete is all that is left (#448)
  const createFailed = kind === 'Error' && item.CreateFailed === true;
  const canReply = !createFailed && (REPLY_KINDS.has(kind) || (kind === 'Permission' && !permission));
  const canMarkSeen = SEEN_KINDS.has(kind) && !(kind === 'Question' && item.Question);

  /** Runs a hub call; the item leaves the inbox by AttentionChanged when it worked. */
  const run = async (call: () => Promise<void>) => {
    setBusy(true);
    setError(null);
    try {
      await call();
      return true;
    } catch (err) {
      setError(hubErrorMessage(err));
      return false;
    } finally {
      setBusy(false);
    }
  };

  const send = async (text: string) => {
    if (!text.trim() || busy) return;
    if (await run(() => replyAndResume(serverId, projectId, text))) setReply('');
  };

  const pick = (q: QuestionItem, label: string) => {
    if (!answersAtOnce) {
      void send(label);
      return;
    }
    const was = chosen[q.Question] ?? [];
    const next = !q.MultiSelect ? [label] : was.includes(label) ? was.filter(l => l !== label) : [...was, label];
    setChosen({ ...chosen, [q.Question]: next });
  };
  const allChosen = pending !== null && pending.Questions.every(q => (chosen[q.Question]?.length ?? 0) > 0);
  const sendAnswers = async () => {
    if (!pending || !allChosen || busy) return;
    // A multi-select's labels in the order offered, joined as the hub takes them
    const byQuestion = Object.fromEntries(pending.Questions.map(q =>
      [q.Question, q.Options.filter(o => chosen[q.Question]?.includes(o.Label)).map(o => o.Label).join(', ')]));
    if (await run(() => answerQuestion(serverId, projectId, pending.RequestId, byQuestion))) setChosen({});
  };

  const answerPermission = async (allow: boolean, message?: string) => {
    if (!permission) return;
    if (await run(() => respondToPermission(serverId, projectId, permission.RequestId, { Allow: allow, Message: message ?? null }))) setDenyMessage('');
  };

  const remove = async () => {
    if (busy) return;
    const project = useAppStore.getState().getConnection(serverId)?.projects.find(p => p.Id === projectId)
      ?? { Id: projectId, Name: item.ProjectName, State: 'Error' as const, UpdatedAt: item.Since, SharedFolder: false, Adopted: false };
    setBusy(true);
    try {
      // The item leaves the inbox by AttentionChanged; a refusal says why in a toast
      await deleteSession(serverId, project);
    } finally {
      setBusy(false);
    }
  };

  const open = (e: React.MouseEvent<HTMLButtonElement>) => {
    // A clicked button keeps the focus (Chromium on Windows, WebView2), and beside the open project this one
    // stays: let go of it, so its question's keys, which are the prompt's or the page's (#240), reach it (#218)
    e.currentTarget.blur();
    selectProject(serverId, projectId);
  };

  const meta = [item.Profile && item.Profile !== 'Default' ? item.Profile : null, serverName, offline ? 'offline' : null, `waiting ${waitingFor(item.Since, now)}`]
    .filter(Boolean).join(' · ');

  return (
    <article className={`inbox-item inbox-kind-${kind}${focused ? ' inbox-item-focused' : ''}${offline ? ' inbox-item-offline' : ''}`}>
      <button className="inbox-item-header" onClick={open} title="Open the project">
        <span className="inbox-item-kind">{kindLabel(item)}</span>
        <span className="inbox-item-name">{item.ProjectName}</span>
        {item.Importance === 'Important' && <ImportanceMark importance="Important" />}
        <span className="inbox-item-meta">{meta}</span>
      </button>

      {permission ? (
        // One card per request: the next one does not start out sending, as the last one was (#218)
        <PermissionCard key={permission.RequestId} serverId={serverId} projectId={projectId} permission={permission} onAnswer={answerPermission} denyMessage={{ value: denyMessage, onChange: setDenyMessage }} />
      ) : pending ? (
        <div className="inbox-questions">
          {pending.Questions.map((q, qi) => (
            <section key={q.Question} className="inbox-question">
              {(pending.Questions.length > 1 || q.Header || q.MultiSelect) && (
                <div className="inbox-question-header">
                  {pending.Questions.length > 1 && <span className="inbox-question-num">{qi + 1}/{pending.Questions.length}</span>}
                  {q.Header && <span className="inbox-question-title">{q.Header}</span>}
                  {q.MultiSelect && <span className="inbox-question-multi">Choose any</span>}
                </div>
              )}
              <div className="inbox-item-text inbox-question-text">{q.Question}</div>
              {q.Options.length > 0 && (
                <div className="inbox-item-options inbox-question-options">
                  {q.Options.map(o => {
                    const selected = chosen[q.Question]?.includes(o.Label) === true;
                    return (
                      <button key={o.Label} className={`btn btn-secondary inbox-option${selected ? ' inbox-option-selected' : ''}`}
                        onClick={() => pick(q, o.Label)} disabled={busy} aria-pressed={answersAtOnce ? selected : undefined}>
                        <span className="inbox-option-label">{o.Label}</span>
                        {o.Description && <span className="inbox-option-desc">{o.Description}</span>}
                      </button>
                    );
                  })}
                </div>
              )}
            </section>
          ))}
          {answersAtOnce && (
            <div className="inbox-item-actions">
              <button className="btn btn-primary" onClick={sendAnswers} disabled={busy || !allChosen}>Send answers</button>
            </div>
          )}
        </div>
      ) : (
        <div className="inbox-item-text">{item.Text}</div>
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

      {/* Every kind can be gone to: the header opens it too, but does not look like a button (#440) */}
      <div className="inbox-item-actions">
        <button className="btn btn-secondary" onClick={open} title="Open the project">Go to</button>
        {canMarkSeen && item.PullRequestUrl && (
          <a className="btn btn-secondary" href={item.PullRequestUrl} target="_blank" rel="noreferrer">{pullRequestLabel(item.PullRequestUrl)}</a>
        )}
        {canMarkSeen && (
          <button className="btn btn-secondary" onClick={() => run(() => markSeen(serverId, projectId))} disabled={busy}>
            Mark seen
          </button>
        )}
        {createFailed && (
          <button className="btn btn-danger" onClick={remove} disabled={busy} title="Its create failed: delete it, or create it again">
            Delete
          </button>
        )}
      </div>

      {error && <div className="inbox-item-error">{error}</div>}
    </article>
  );
}

/** How long since `since`: "<1m", "5m", "3h", "2d". */
/** The link to the project's pull request, which is there already: "View PR #533", never "Open PR", read as creating one (#535). */
function pullRequestLabel(url: string): string {
  const number = /\/pull\/(\d+)/.exec(url)?.[1];
  return number ? `View PR #${number}` : 'View PR';
}

function waitingFor(since: string, now: number): string {
  const min = Math.floor((now - new Date(since).getTime()) / 60000);
  if (min < 1) return '<1m';
  if (min < 60) return `${min}m`;
  const hr = Math.floor(min / 60);
  return hr < 24 ? `${hr}h` : `${Math.floor(hr / 24)}d`;
}
