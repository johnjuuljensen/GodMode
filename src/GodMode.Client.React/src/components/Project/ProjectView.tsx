import { useEffect, useRef, useState, useCallback, useLayoutEffect, useMemo, useSyncExternalStore } from 'react';
import { useAppStore, transcriptKey } from '../../store';
import { ChatMessage } from './ChatMessage';
import { QuestionPrompt } from './QuestionPrompt';
import { PermissionCard } from './PermissionCard';
import { confirmAction } from '../../confirmDialog';
import './ProjectView.css';

const SIMPLE_VIEW_KEY = 'godmode-simple-view';

// A touch-first device, whose on-screen keyboard has no Shift+Enter (a narrow laptop window is not one)
const touchKeyboardQuery = window.matchMedia('(hover: none) and (pointer: coarse)');
const subscribeTouchKeyboard = (onChange: () => void) => {
  touchKeyboardQuery.addEventListener('change', onChange);
  return () => touchKeyboardQuery.removeEventListener('change', onChange);
};
const getTouchKeyboard = () => touchKeyboardQuery.matches;

interface Props {
  serverId: string;
  projectId: string;
}

export function ProjectView({ serverId, projectId }: Props) {
  const conn = useAppStore(s => s.serverConnections.find(c => c.serverInfo.Id === serverId));
  const outputMessages = useAppStore(s => s.outputMessages);
  const question = useAppStore(s => s.question);
  const dismissQuestion = useAppStore(s => s.dismissQuestion);
  const markInputSent = useAppStore(s => s.markInputSent);
  const respondToPermission = useAppStore(s => s.respondToPermission);
  const answerQuestion = useAppStore(s => s.answerQuestion);
  const replyAndResume = useAppStore(s => s.replyAndResume);
  const [inputText, setInputText] = useState('');
  const [projectName, setProjectName] = useState('');
  const [simpleView, setSimpleView] = useState(() => localStorage.getItem(SIMPLE_VIEW_KEY) !== 'false');
  const messagesRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLTextAreaElement>(null);
  const touchKeyboard = useSyncExternalStore(subscribeTouchKeyboard, getTouchKeyboard);

  // Grows with its content up to the CSS max-height, then scrolls
  useLayoutEffect(() => {
    const el = inputRef.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = `${el.scrollHeight + el.offsetHeight - el.clientHeight}px`;
  }, [inputText]);

  // Loading until the server says the replay is complete, unless a transcript is already held
  const transcriptPhase = useAppStore(s => s.transcripts[transcriptKey(serverId, projectId)]?.phase);
  const phase: 'loading' | 'ready' = transcriptPhase !== 'live' && outputMessages.length === 0 ? 'loading' : 'ready';
  const subscribeOutput = useAppStore(s => s.subscribeOutput);
  const unsubscribeOutput = useAppStore(s => s.unsubscribeOutput);

  const hub = conn?.hub;
  const project = conn?.projects.find(p => p.Id === projectId);

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

  useLayoutEffect(() => {
    if (phase !== 'ready') return;
    const el = messagesRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [phase, outputMessages.length]);

  const toggleSimpleView = useCallback(() => {
    setSimpleView(v => {
      const next = !v;
      localStorage.setItem(SIMPLE_VIEW_KEY, String(next));
      return next;
    });
  }, []);

  const visibleMessages = useMemo(
    () => simpleView
      ? outputMessages.filter(m => m.type !== 'system' && m.type !== 'result')
      : outputMessages,
    [outputMessages, simpleView],
  );

  const state = project?.State ?? 'Idle';
  const canSendInput = state === 'WaitingInput' || state === 'WaitingPermission' || state === 'Running' || state === 'Stopped' || state === 'Idle';
  const canResume = state === 'Stopped' || state === 'Idle';
  const canStop = state === 'Running' || state === 'WaitingInput' || state === 'WaitingPermission';

  // What claude is blocked on: a tool call to allow or deny, or AskUserQuestion's questions, asked one at a time
  const pendingPermission = project?.PendingPermission ?? null;
  const pendingQuestion = project?.PendingQuestion ?? null;
  const [answers, setAnswers] = useState<{ requestId: string; byQuestion: Record<string, string> } | null>(null);
  const answered = useMemo(
    () => (answers && answers.requestId === pendingQuestion?.RequestId ? answers.byQuestion : {}),
    [answers, pendingQuestion?.RequestId],
  );
  const openQuestion = pendingQuestion?.Questions.find(q => answered[q.Question] === undefined) ?? null;

  const handlePermission = useCallback(async (allow: boolean) => {
    if (!pendingPermission) return;
    try {
      await respondToPermission(serverId, projectId, pendingPermission.RequestId, { Allow: allow });
    } catch (err) {
      console.error('Failed to answer the permission request:', err);
    }
  }, [pendingPermission, respondToPermission, serverId, projectId]);

  const handleQuestionAnswer = useCallback(async (label: string) => {
    if (!pendingQuestion || !openQuestion) return;
    const byQuestion = { ...answered, [openQuestion.Question]: label };
    setAnswers({ requestId: pendingQuestion.RequestId, byQuestion });
    if (pendingQuestion.Questions.some(q => byQuestion[q.Question] === undefined)) return;
    try {
      await answerQuestion(serverId, projectId, pendingQuestion.RequestId, byQuestion);
    } catch (err) {
      console.error('Failed to answer the question:', err);
    }
  }, [pendingQuestion, openQuestion, answered, answerQuestion, serverId, projectId]);

  const sendText = useCallback(async (text: string) => {
    if (!text.trim()) return;
    markInputSent();
    try {
      // The server resumes a stopped project and sends once claude runs
      await replyAndResume(serverId, projectId, text);
    } catch (err) {
      console.error('Failed to send input:', err);
    }
  }, [replyAndResume, serverId, projectId, markInputSent]);

  const handleSendInput = async () => {
    if (!inputText.trim()) return;
    const text = inputText;
    setInputText('');
    await sendText(text);
  };

  const handleOptionSelect = useCallback((label: string) => {
    sendText(label);
    inputRef.current?.focus();
  }, [sendText]);

  const handleDismiss = useCallback(() => {
    dismissQuestion();
    inputRef.current?.focus();
  }, [dismissQuestion]);

  const handleStop = async () => {
    // A stray tap on the status pill must never end a session
    if (!hub || !await confirmAction(`Stop "${projectName}"?`, 'Stop', { message: 'Claude stops mid-turn. Sending a message resumes it.', tone: 'danger' })) return;
    try { await hub.stopProject(projectId); } catch (err) { console.error(err); }
  };

  const handleResume = async () => {
    if (!hub) return;
    try { await hub.resumeProject(projectId); } catch (err) { console.error(err); }
  };

  const [showProjectMenu, setShowProjectMenu] = useState(false);
  const projectMenuRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!showProjectMenu) return;
    const handler = (e: MouseEvent) => {
      if (projectMenuRef.current && !projectMenuRef.current.contains(e.target as Node)) setShowProjectMenu(false);
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [showProjectMenu]);

  const handleArchive = async () => {
    if (!hub) return;
    setShowProjectMenu(false);
    try { await hub.archiveProject(projectId); } catch (err) { console.error(err); }
  };

  const handleDelete = async () => {
    setShowProjectMenu(false);
    if (!hub || !await confirmAction(`Delete "${projectName}" permanently?`, 'Delete', { message: 'This cannot be undone.', tone: 'danger' })) return;
    try { await hub.deleteProject(projectId, state === 'Running'); } catch (err) { console.error(err); }
  };

  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    // Enter that confirms an IME composition is not a send (Safari reports it after compositionend, as keyCode 229)
    if (e.key !== 'Enter' || e.nativeEvent.isComposing || e.keyCode === 229) return;
    // Plain Enter sends. A touch keyboard has no Shift+Enter, so there every Enter is a newline and the Send button sends
    if (!touchKeyboard && !(e.shiftKey || e.ctrlKey || e.metaKey || e.altKey)) {
      e.preventDefault();
      handleSendInput();
    } else if (e.ctrlKey || e.metaKey || e.altKey) {
      // Shift+Enter and plain Enter insert a newline natively; Ctrl/Cmd/Alt+Enter insert nothing
      e.preventDefault();
      const el = e.currentTarget;
      el.setRangeText('\n', el.selectionStart, el.selectionEnd, 'end');
      setInputText(el.value);
    }
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
            <span className="project-status-label">{state}</span>
            {canStop && <span className="project-status-action">Stop</span>}
            {canResume && <span className="project-status-action">Resume</span>}
          </button>
          <div className="project-menu-container" ref={projectMenuRef}>
            <button className="delete-btn" onClick={() => setShowProjectMenu(!showProjectMenu)} title="Archive or delete">
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <polyline points="3 6 5 6 21 6" /><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" />
              </svg>
            </button>
            {showProjectMenu && (
              <div className="project-menu-dropdown">
                <button className="project-menu-item" onClick={handleArchive}>
                  <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                    <polyline points="21 8 21 21 3 21 3 8" /><rect x="1" y="3" width="22" height="5" /><line x1="10" y1="12" x2="14" y2="12" />
                  </svg>
                  Archive
                </button>
                <button className="project-menu-item project-menu-item-danger" onClick={handleDelete}>
                  <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                    <polyline points="3 6 5 6 21 6" /><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" />
                  </svg>
                  Delete permanently
                </button>
              </div>
            )}
          </div>
        </div>
      </div>

      <div className="project-messages" ref={messagesRef}>
        {phase === 'loading' ? (
          <div className="project-messages-empty">Loading...</div>
        ) : visibleMessages.length === 0 ? (
          <div className="project-messages-empty">
            {conn?.connectionState === 'connected' ? 'Waiting for output...' : 'Not connected'}
          </div>
        ) : (
          visibleMessages.map((msg, i) => <ChatMessage key={i} message={msg} />)
        )}
      </div>

      {pendingPermission ? (
        <PermissionCard permission={pendingPermission} onAnswer={handlePermission} />
      ) : openQuestion ? (
        <QuestionPrompt
          text={openQuestion.Question}
          header={openQuestion.Header ?? null}
          options={openQuestion.Options}
          onSelectOption={handleQuestionAnswer}
          onDismiss={handleDismiss}
        />
      ) : question.isActive && (
        <QuestionPrompt
          text={question.text}
          header={question.header}
          options={[]}
          onSelectOption={handleOptionSelect}
          onDismiss={handleDismiss}
        />
      )}

      <div className="project-input-bar">
        <textarea
          ref={inputRef}
          rows={1}
          className="project-input"
          value={inputText}
          onChange={e => setInputText(e.target.value)}
          onKeyDown={handleKeyDown}
          enterKeyHint={touchKeyboard ? 'enter' : 'send'}
          placeholder={canResume ? 'Type to resume...' : 'Type your response...'}
          disabled={!canSendInput}
        />
        <button className="btn btn-primary" onClick={handleSendInput} disabled={!canSendInput || !inputText.trim()}>
          Send
        </button>
      </div>
    </div>
  );
}
