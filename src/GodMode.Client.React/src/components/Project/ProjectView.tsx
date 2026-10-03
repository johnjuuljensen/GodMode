import { useEffect, useRef, useState, useCallback, useMemo } from 'react';
import { useAppStore, transcriptKey } from '../../store';
import { TranscriptList, type TranscriptListHandle } from './TranscriptList';
import { createTranscriptBuilder } from '../../signalr/parseMessage';
import { QuestionPrompt } from './QuestionPrompt';
import { PermissionCard } from './PermissionCard';
import { ReplyInput } from './ReplyInput';
import { hubErrorMessage } from '../../signalr/hubError';
import { isConversation } from './transcriptRow';
import { confirmAction } from '../../confirmDialog';
import { deleteSession } from '../../deleteSession';
import './ProjectView.css';

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
  const replyAndResume = useAppStore(s => s.replyAndResume);
  const [inputText, setInputText] = useState('');
  const [projectName, setProjectName] = useState('');
  const [simpleView, setSimpleView] = useState(() => localStorage.getItem(SIMPLE_VIEW_KEY) !== 'false');
  const inputRef = useRef<HTMLTextAreaElement>(null);
  const transcriptRef = useRef<TranscriptListHandle>(null);

  // Loading until the server says the replay is complete, unless a transcript is already held
  const transcriptPhase = useAppStore(s => s.transcripts[transcriptKey(serverId, projectId)]?.phase);
  const phase: 'loading' | 'ready' = transcriptPhase !== 'live' && outputMessages.length === 0 ? 'loading' : 'ready';
  const subscribeOutput = useAppStore(s => s.subscribeOutput);
  const unsubscribeOutput = useAppStore(s => s.unsubscribeOutput);

  const hub = conn?.hub;
  const project = conn?.projects.find(p => p.Id === projectId);
  // Connected, the server's list taken on this connection, and the project not in it: deleted (here,
  // elsewhere, or while this client slept), or a link to one it does not have. Nothing here acts on it (#239)
  const projectsListed = useAppStore(s => !!s.projectsListed[serverId]);
  const notFound = conn?.connectionState === 'connected' && projectsListed && !project;

  useEffect(() => {
    // Resumes from the transcript held, so reopening only adds what is new. Open while it shows: the
    // store subscribes it again whenever the server reconnects
    subscribeOutput(serverId, projectId).catch(console.error);
    return () => {
      unsubscribeOutput(serverId, projectId).catch(console.error);
    };
  }, [hub, serverId, projectId, subscribeOutput, unsubscribeOutput]);

  useEffect(() => {
    if (project) setProjectName(project.Name);
  }, [project]);

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

  const state = project?.State ?? 'Idle';
  // Created with no prompt (#352): Idle with its output loaded and empty, claude has had no turn and
  // waits for the first message. Resume has nothing to do
  const awaitsFirstMessage = !notFound && state === 'Idle' && phase === 'ready' && outputMessages.length === 0;
  const canResume = !notFound && (state === 'Stopped' || state === 'Idle' && !awaitsFirstMessage);
  const canStop = !notFound && (state === 'Running' || state === 'WaitingInput' || state === 'WaitingPermission');

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
      await replyAndResume(serverId, projectId, text);
    } catch (err) {
      console.error('Failed to send input:', err);
      setRefusal({ projectId, message: hubErrorMessage(err) });
      // What was typed comes back to be changed, unless something else has been typed since
      setInputText(current => current === '' ? text : current);
    }
  }, [replyAndResume, serverId, projectId]);

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
    if (!hub || !await confirmAction(`Stop "${projectName}"?`, 'Stop', { message: 'Claude stops mid-turn. Sending a message resumes it.', tone: 'danger' })) return;
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
          {(project?.ProfileName || project?.RootName) && (
            <span className="project-header-root">
              {project?.ProfileName && project.ProfileName !== 'Default' ? project.ProfileName : ''}
              {project?.ProfileName && project.ProfileName !== 'Default' && project?.RootName ? ' / ' : ''}
              {project?.RootName ?? ''}
            </span>
          )}
        </div>
        <div className="project-header-actions">
          <button
            className={`btn btn-toggle ${simpleView ? 'active' : ''}`}
            onClick={toggleSimpleView}
            title={simpleView ? 'Show all messages' : 'Hide system & result messages'}
          >
            {simpleView ? 'Simple' : 'Full'}
          </button>
          <button
            className={`project-status-btn ${state}`}
            onClick={canStop ? handleStop : canResume ? handleResume : undefined}
            disabled={!canStop && !canResume}
            title={canStop ? 'Click to stop' : canResume ? 'Click to resume' : state}
          >
            <span className="project-status-dot" />
            <span className="project-status-label">{notFound ? 'Not found' : state}</span>
            {canStop && <span className="project-status-action">Stop</span>}
            {canResume && <span className="project-status-action">Resume</span>}
          </button>
          <button className="delete-btn" onClick={handleDelete} disabled={notFound || !project} title={project?.SharedFolder ? 'Delete (undo for a few seconds)' : 'Delete permanently'}>
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <polyline points="3 6 5 6 21 6" /><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" />
            </svg>
          </button>
        </div>
      </div>

      {!notFound && phase === 'ready' && visibleItems.length > 0 ? (
        <TranscriptList ref={transcriptRef} key={transcriptKey(serverId, projectId)} items={visibleItems} />
      ) : (
        <div className="project-messages">
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
            : canResume || state === 'Error' ? 'Type to resume...' : 'Type your response...'}
          disabled={notFound}
          commands={project?.SlashCommands}
        />
        <button className="btn btn-primary" onClick={handleSendInput} disabled={notFound || !inputText.trim()}>
          Send
        </button>
      </div>
    </div>
  );
}
