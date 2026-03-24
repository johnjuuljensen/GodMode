import { useEffect, useRef, useState, useCallback, useLayoutEffect, useMemo } from 'react';
import { useAppStore } from '../../store';
import { ChatMessage } from './ChatMessage';
import { QuestionPrompt } from './QuestionPrompt';
import './ProjectView.css';

const SIMPLE_VIEW_KEY = 'godmode-simple-view';

interface Props {
  serverIndex: number;
  projectId: string;
}

export function ProjectView({ serverIndex, projectId }: Props) {
  const server = useAppStore(s => s.servers[serverIndex]);
  const outputMessages = useAppStore(s => s.outputMessages);
  const clearOutput = useAppStore(s => s.clearOutput);
  const question = useAppStore(s => s.question);
  const dismissQuestion = useAppStore(s => s.dismissQuestion);
  const markInputSent = useAppStore(s => s.markInputSent);
  const [inputText, setInputText] = useState('');
  const [projectName, setProjectName] = useState('');
  const [simpleView, setSimpleView] = useState(() => localStorage.getItem(SIMPLE_VIEW_KEY) !== 'false');
  const messagesRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  // Phase: 'loading' = messages streaming in (not rendered), 'ready' = render + show
  const [phase, setPhase] = useState<'loading' | 'ready'>('loading');
  const settleTimerRef = useRef<ReturnType<typeof setTimeout>>(undefined);

  const hub = server?.hub;
  const project = server?.projects.find(p => p.Id === projectId);

  // Subscribe to project output on mount
  useEffect(() => {
    if (!hub || server?.connectionState !== 'connected') return;
    clearOutput();
    setPhase('loading');

    // Give messages time to stream in before rendering them
    if (settleTimerRef.current) clearTimeout(settleTimerRef.current);
    settleTimerRef.current = setTimeout(() => setPhase('ready'), 800);

    hub.subscribeProject(projectId, 0).catch(console.error);
    return () => {
      hub.unsubscribeProject(projectId).catch(console.error);
      if (settleTimerRef.current) clearTimeout(settleTimerRef.current);
    };
  }, [hub, projectId, server?.connectionState, clearOutput]);

  // Update project name
  useEffect(() => {
    if (project) setProjectName(project.Name);
  }, [project]);

  // When phase becomes 'ready', all messages render in one batch.
  // useLayoutEffect fires BEFORE the browser paints — snap scroll to bottom.
  // Also snaps on each new message after initial load.
  useLayoutEffect(() => {
    if (phase !== 'ready') return;
    const el = messagesRef.current;
    if (el) {
      el.scrollTop = el.scrollHeight;
    }
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
  const canSendInput = state === 'WaitingInput' || state === 'Running' || state === 'Stopped' || state === 'Idle';
  const canResume = state === 'Stopped' || state === 'Idle';
  const canStop = state === 'Running' || state === 'WaitingInput';

  const sendText = useCallback(async (text: string) => {
    if (!text.trim() || !hub) return;
    markInputSent();
    try {
      if (state === 'Stopped' || state === 'Idle') {
        await hub.resumeProject(projectId);
        await new Promise(r => setTimeout(r, 500));
      }
      await hub.sendInput(projectId, text);
    } catch (err) {
      console.error('Failed to send input:', err);
    }
  }, [hub, projectId, state, markInputSent]);

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
    if (!hub) return;
    try { await hub.stopProject(projectId); } catch (err) { console.error(err); }
  };

  const handleResume = async () => {
    if (!hub) return;
    try { await hub.resumeProject(projectId); } catch (err) { console.error(err); }
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      handleSendInput();
    }
  };

  return (
    <div className="project-view">
      <div className="project-header">
        <div className="project-header-info">
          <span className={`project-state-badge ${state}`}>{state}</span>
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
          {canStop && (
            <button className="btn btn-secondary" onClick={handleStop}>Stop</button>
          )}
          {canResume && (
            <button className="btn btn-primary" onClick={handleResume}>Resume</button>
          )}
        </div>
      </div>

      <div className="project-messages" ref={messagesRef}>
        {phase === 'loading' ? (
          <div className="project-messages-empty">Loading…</div>
        ) : visibleMessages.length === 0 ? (
          <div className="project-messages-empty">
            {server?.connectionState === 'connected'
              ? 'Waiting for output...'
              : 'Not connected'}
          </div>
        ) : (
          visibleMessages.map((msg, i) => (
            <ChatMessage key={i} message={msg} />
          ))
        )}
      </div>

      {/* Question prompt with option selector */}
      {question.isActive && (
        <QuestionPrompt
          text={question.text}
          header={question.header}
          options={question.options}
          onSelectOption={handleOptionSelect}
          onDismiss={handleDismiss}
        />
      )}

      <div className="project-input-bar">
        <input
          ref={inputRef}
          type="text"
          className="project-input"
          value={inputText}
          onChange={e => setInputText(e.target.value)}
          onKeyDown={handleKeyDown}
          placeholder={canResume ? 'Type to resume...' : 'Type your response...'}
          disabled={!canSendInput}
        />
        <button
          className="btn btn-primary"
          onClick={handleSendInput}
          disabled={!canSendInput || !inputText.trim()}
        >
          Send
        </button>
      </div>
    </div>
  );
}
