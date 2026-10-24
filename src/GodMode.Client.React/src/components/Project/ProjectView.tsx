import { useEffect, useLayoutEffect, useRef, useState, useCallback, useMemo } from 'react';
import { useAppStore, transcriptKey, rootShown } from '../../store';
import { TranscriptList, type TranscriptListHandle } from './TranscriptList';
import { createTranscriptBuilder, type TranscriptItem } from '../../signalr/parseMessage';
import { QuestionPrompt } from './QuestionPrompt';
import { PermissionCard } from './PermissionCard';
import { ReplyInput } from './ReplyInput';
import { hubErrorMessage } from '../../signalr/hubError';
import { isConversation } from './transcriptRow';
import { confirmAction } from '../../confirmDialog';
import { deleteSession } from '../../deleteSession';
import { DeletingNote } from '../Deleting/Deleting';
import { useClickToCompose } from './clickToCompose';
import { focusOnOpen } from './focusOnOpen';
import './ProjectView.css';
import { ImportancePicker } from '../Importance/Importance';
import { BackgroundBadge } from '../BackgroundTasks/BackgroundTasks';
import { ProgressMark } from '../Progress/Progress';
import { backgroundTitle, inBackground } from '../BackgroundTasks/backgroundState';

const SIMPLE_VIEW_KEY = 'godmode-simple-view';

interface Props {
  serverId: string;
  projectId: string;
}

export function ProjectView({ serverId, projectId }: Props) {
  const conn = useAppStore(s => s.serverConnections.find(c => c.serverInfo.Id === serverId));
  const outputMessages = useAppStore(s => s.outputMessages);
  const respondToPermission = useAppStore(s => s.respondToPermission);
  const answerQuestion = useAppStore(s => s.answerQuestion);
  const sendReply = useAppStore(s => s.sendReply);
  const pendingSends = useAppStore(s => s.pendingSends[transcriptKey(serverId, projectId)]);
  // The store's, so it is still there when the session is opened again (#581)
  const inputText = useAppStore(s => s.composerDrafts[transcriptKey(serverId, projectId)] ?? '');
  const setComposerDraft = useAppStore(s => s.setComposerDraft);
  const setInputText = useCallback((text: string) => setComposerDraft(serverId, projectId, text), [setComposerDraft, serverId, projectId]);
  const [projectName, setProjectName] = useState('');
  const [simpleView, setSimpleView] = useState(() => localStorage.getItem(SIMPLE_VIEW_KEY) !== 'false');
  const inputRef = useRef<HTMLTextAreaElement>(null);
  // A draft opened again has its cursor at its end
  useLayoutEffect(() => {
    const el = inputRef.current;
    if (el) el.selectionStart = el.selectionEnd = el.value.length;
  }, []);
  const transcriptRef = useRef<TranscriptListHandle>(null);

  // Loading until the server says the replay is complete, unless a transcript is already held
  const transcriptPhase = useAppStore(s => s.transcripts[transcriptKey(serverId, projectId)]?.phase);
  const phase: 'loading' | 'ready' = transcriptPhase !== 'live' && outputMessages.length === 0 ? 'loading' : 'ready';
  const subscribeOutput = useAppStore(s => s.subscribeOutput);
  const unsubscribeOutput = useAppStore(s => s.unsubscribeOutput);

  const hub = conn?.hub;
  const project = conn?.projects.find(p => p.Id === projectId);
  // Its root as it is shown (#434): the title, else the name
  const rootLabel = useAppStore(s => project?.RootName ? rootShown(s.serverConnections.find(c => c.serverInfo.Id === serverId)?.roots, project.ProfileName, project.RootName) : '');
  // Connected, the server's list taken on this connection, and the project not in it: deleted (here,
  // elsewhere, or while this client slept), or a link to one it does not have. Nothing here acts on it (#239)
  const projectsListed = useAppStore(s => !!s.projectsListed[serverId]);
  const notFound = conn?.connectionState === 'connected' && projectsListed && !project;
  // Being deleted (#599): it is read, not acted on, until it leaves the list or its delete fails
  const deleting = project?.Deleting === true;
  const setHidden = useAppStore(s => s.setHidden);

  useEffect(() => {
    // Resumes from the transcript held, so reopening only adds what is new. Open while it shows: the
    // store subscribes it again whenever the server reconnects
    subscribeOutput(serverId, projectId).catch(console.error);
    return () => {
      unsubscribeOutput(serverId, projectId).catch(console.error);
    };
  }, [hub, serverId, projectId, subscribeOutput, unsubscribeOutput]);

  // Set while rendering, not in an effect: the name shown stays the last one known once the project is gone
  if (project && project.Name !== projectName) setProjectName(project.Name);

  const toggleSimpleView = useCallback(() => {
    setSimpleView(v => {
      const next = !v;
      localStorage.setItem(SIMPLE_VIEW_KEY, String(next));
      return next;
    });
  }, []);

  // Built incrementally: a new line adds its items, and every other item stays the same object
  const [buildTranscript] = useState(createTranscriptBuilder);
  const transcript = useMemo(() => buildTranscript(outputMessages), [buildTranscript, outputMessages]);
  const visibleItems = useMemo(
    () => simpleView ? transcript.filter(isConversation) : transcript,
    [transcript, simpleView],
  );
  // What was sent from here and not echoed yet shows at once, after everything claude has written (#383)
  const shownItems = useMemo((): TranscriptItem[] => pendingSends
    ? [...visibleItems, ...pendingSends.map(p => ({
      kind: 'userText' as const, key: `pending-${p.id}`, text: p.text, pending: p.notTaken ? 'notTaken' as const : 'waiting' as const,
    }))]
    : visibleItems, [visibleItems, pendingSends]);

  const state = project?.State ?? 'Idle';
  // Created with no prompt (#352): Idle with its output loaded and empty, claude has had no turn and
  // waits for the first message. Resume has nothing to do
  const awaitsFirstMessage = !notFound && state === 'Idle' && phase === 'ready' && outputMessages.length === 0;
  // Idle, with claude still at work in the background (#432): shown as working, and its stop ends that work
  const background = !notFound && !!project && inBackground(project);
  // Idle stops too (#580), its first message awaited or not: a session is stopped so its next launch reads the
  // root's config again, and a message relaunches it. One Idle with no process (the server restarted) just says Stopped
  const canResume = !notFound && state === 'Stopped';
  const canStop = !notFound && (background || state === 'Idle' || state === 'Running' || state === 'WaitingInput' || state === 'WaitingPermission');

  // What claude is blocked on: a tool call to allow or deny, or AskUserQuestion's questions, asked one at a time
  const pendingPermission = project?.PendingPermission ?? null;
  const pendingQuestion = project?.PendingQuestion ?? null;
  const [answers, setAnswers] = useState<{ requestId: string; byQuestion: Record<string, string> } | null>(null);
  const answered = useMemo(
    () => (answers && answers.requestId === pendingQuestion?.RequestId ? answers.byQuestion : {}),
    [answers, pendingQuestion?.RequestId],
  );
  const openQuestion = pendingQuestion?.Questions.find(q => answered[q.Question] === undefined) ?? null;
  // Why the last answer to a question failed (another client answered first): that question's alone
  const [answerError, setAnswerError] = useState<{ requestId: string; message: string } | null>(null);
  const questionError = answerError && answerError.requestId === pendingQuestion?.RequestId ? answerError.message : null;
  // Why the server refused the last message sent (a slash command it does not pass, #31): that project's alone,
  // and cleared by the next send
  const [refusal, setRefusal] = useState<{ projectId: string; message: string } | null>(null);
  const sendError = refusal?.projectId === projectId ? refusal.message : null;
  // A click in the output focuses the composer, unless a question's options take the keys (#435, #240)
  const keysTaken = !pendingPermission && (openQuestion?.Options.length ?? 0) > 0;
  const clickToCompose = useClickToCompose(inputRef, keysTaken);
  // Opened, the composer takes the keys (#559): the view is keyed by project, so this is each selection's
  const keysTakenOnOpen = useRef(keysTaken);
  useEffect(() => focusOnOpen(inputRef.current, keysTakenOnOpen.current), []);

  // A failure (another client answered first, claude stopped waiting) is the card's to show
  const handlePermission = useCallback(async (allow: boolean) => {
    if (!pendingPermission) return;
    await respondToPermission(serverId, projectId, pendingPermission.RequestId, { Allow: allow });
  }, [pendingPermission, respondToPermission, serverId, projectId]);

  // Any question of the request may be answered, or its answer changed, until the last is chosen (#454)
  const handleQuestionAnswer = useCallback(async (questionText: string, label: string) => {
    if (!pendingQuestion || !openQuestion) return;
    const byQuestion = { ...answered, [questionText]: label };
    setAnswers({ requestId: pendingQuestion.RequestId, byQuestion });
    if (pendingQuestion.Questions.some(q => byQuestion[q.Question] === undefined)) return;
    setAnswerError(null);
    try {
      await answerQuestion(serverId, projectId, pendingQuestion.RequestId, byQuestion);
    } catch (err) {
      // Asked again, if it still waits: the answer did not reach claude
      setAnswers(null);
      setAnswerError({ requestId: pendingQuestion.RequestId, message: err instanceof Error ? err.message : String(err) });
    }
  }, [pendingQuestion, openQuestion, answered, answerQuestion, serverId, projectId]);

  const sendText = useCallback(async (text: string) => {
    if (!text.trim()) return;
    setRefusal(null);
    // The reader's own message is one they want to see, wherever they had scrolled to
    transcriptRef.current?.scrollToLatest();
    try {
      // The server resumes a stopped project and sends once claude runs
      await sendReply(serverId, projectId, text);
    } catch (err) {
      console.error('Failed to send input:', err);
      setRefusal({ projectId, message: hubErrorMessage(err) });
      // What was typed comes back to be changed, unless something else has been typed since
      if (!useAppStore.getState().composerDrafts[transcriptKey(serverId, projectId)]) setInputText(text);
    }
  }, [sendReply, serverId, projectId, setInputText]);

  const handleSendInput = async () => {
    if (notFound || !inputText.trim()) return;
    const text = inputText;
    setInputText('');
    await sendText(text);
  };

  // The question still waits: dismissing it goes to the composer, which answers it too
  const handleDismiss = useCallback(() => inputRef.current?.focus(), []);

  const handleStop = async () => {
    // A stray tap on the status pill must never end a session
    if (!hub || !await confirmAction(`Stop "${projectName}"?`, 'Stop', {
      message: state === 'Idle' && !background ? 'Sending a message resumes it.' : 'Claude stops mid-turn. Sending a message resumes it.',
      tone: 'danger',
    })) return;
    try { await hub.stopProject(projectId); } catch (err) { console.error(err); }
  };

  const handleResume = async () => {
    if (!hub) return;
    try { await hub.resumeProject(projectId); } catch (err) { console.error(err); }
  };

  // As from its row (#325): a session that shares its folder goes at once, with Undo; a worktree asks first
  const handleDelete = async () => {
    if (project) await deleteSession(serverId, project);
  };

  return (
    <div className="project-view">
      <div className="project-header">
        <div className="project-header-info">
          <span className="project-header-name">{projectName}</span>
          {/* Background work while a turn runs or waits on the user; idle, the status button says it */}
          {!background && <BackgroundBadge tasks={project?.BackgroundTasks} />}
          <ProgressMark progress={project?.Progress} bar />
          {(project?.ProfileName || project?.RootName) && (
            <span className="project-header-root" title={project?.RootName ?? undefined}>
              {project?.ProfileName && project.ProfileName !== 'Default' ? project.ProfileName : ''}
              {project?.ProfileName && project.ProfileName !== 'Default' && project?.RootName ? ' / ' : ''}
              {rootLabel}
            </span>
          )}
        </div>
        <div className="project-header-actions">
          {project && !deleting && <ImportancePicker serverId={serverId} projectId={projectId} importance={project.Importance} />}
          {/* Hidden (#598): it folds under its group's older, whatever its state, and is shown again from here or its row */}
          {project && !deleting && (
            <button
              className={`btn btn-toggle ${project.Hidden ? 'active' : ''}`}
              onClick={() => void setHidden(serverId, projectId, !project.Hidden).catch(err => console.error('Failed to hide or show the project:', err))}
              title={project.Hidden ? 'Show it in the list again' : 'Hide it under older in the list'}
            >
              {project.Hidden ? 'Unhide' : 'Hide'}
            </button>
          )}
          <button
            className={`btn btn-toggle ${simpleView ? 'active' : ''}`}
            onClick={toggleSimpleView}
            title={simpleView ? 'Show all messages' : 'Hide system & result messages'}
          >
            {simpleView ? 'Simple' : 'Full'}
          </button>
          <button
            className={`project-status-btn ${background ? 'Running Background' : state}`}
            onClick={deleting ? undefined : canStop ? handleStop : canResume ? handleResume : undefined}
            disabled={deleting || !canStop && !canResume}
            title={background ? `${backgroundTitle(project.BackgroundTasks!)}
Click to stop` : canStop ? 'Click to stop' : canResume ? 'Click to resume' : state}
          >
            <span className="project-status-dot" />
            <span className="project-status-label">
              {notFound ? 'Not found' : background ? `Background · ${project.BackgroundTasks!.length}` : state}
            </span>
            {canStop && !deleting && <span className="project-status-action">Stop</span>}
            {canResume && !deleting && <span className="project-status-action">Resume</span>}
          </button>
          <button className="delete-btn" onClick={handleDelete} disabled={notFound || !project || deleting} title={project?.SharedFolder ? 'Delete (undo for a few seconds)' : 'Delete permanently'}>
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <polyline points="3 6 5 6 21 6" /><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" />
            </svg>
          </button>
        </div>
      </div>

      {deleting && (
        <div className="project-deleting deleting">
          <DeletingNote serverId={serverId} projectId={projectId} lines={10} />
        </div>
      )}

      {!notFound && phase === 'ready' && shownItems.length > 0 ? (
        <TranscriptList ref={transcriptRef} key={transcriptKey(serverId, projectId)} items={shownItems} {...clickToCompose} />
      ) : (
        <div className="project-messages" {...clickToCompose}>
          <div className="project-messages-empty">
            {notFound ? 'Project not found'
              : phase === 'loading' ? 'Loading...' : conn?.connectionState !== 'connected' ? 'Not connected'
              : awaitsFirstMessage ? 'Send the first message to start.' : 'Waiting for output...'}
          </div>
        </div>
      )}

      {pendingPermission ? (
        // One card per request: each fetches its own detail (#234)
        <PermissionCard key={pendingPermission.RequestId} serverId={serverId} projectId={projectId}
          permission={pendingPermission} onAnswer={handlePermission} />
      ) : openQuestion && (
        // A question in plain text has no prompt: it is the transcript's last line, and the composer answers it (#447)
        <QuestionPrompt
          requestId={pendingQuestion!.RequestId}
          questions={pendingQuestion!.Questions}
          answered={answered}
          onAnswer={handleQuestionAnswer}
          onDismiss={handleDismiss}
        />
      )}

      {questionError && <div className="project-answer-error">{questionError}</div>}
      {sendError && <div className="project-answer-error" role="alert">{sendError}</div>}

      <div className="project-input-bar">
        <ReplyInput
          inputRef={inputRef}
          className="project-input"
          value={inputText}
          onChange={setInputText}
          onSubmit={handleSendInput}
          // Every state takes a reply: ReplyAndResume resumes a claude that is not running, one that failed
          // too, as the inbox answers an Error item (#240)
          placeholder={awaitsFirstMessage ? 'Type the first message...'
            : canResume || state === 'Error' || state === 'Idle' && !background ? 'Type to resume...' : 'Type your response...'}
          disabled={notFound || deleting}
          commands={project?.SlashCommands}
        />
        <button className="btn btn-primary" onClick={handleSendInput} disabled={notFound || deleting || !inputText.trim()}>
          Send
        </button>
      </div>
    </div>
  );
}
