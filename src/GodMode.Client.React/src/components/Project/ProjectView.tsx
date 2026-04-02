import { useEffect, useRef, useState, useCallback, useLayoutEffect, useMemo } from 'react';
import { useAppStore } from '../../store';
import { ChatMessage } from './ChatMessage';
import { QuestionPrompt } from './QuestionPrompt';
import './ProjectView.css';

const SIMPLE_VIEW_KEY = 'godmode-simple-view';

interface Props {
  serverId: string;
  projectId: string;
}

export function ProjectView({ serverId, projectId }: Props) {
  const conn = useAppStore(s => s.serverConnections.find(c => c.serverInfo.Id === serverId));
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

  const [phase, setPhase] = useState<'loading' | 'ready'>('loading');
  const settleTimerRef = useRef<ReturnType<typeof setTimeout>>(undefined);

  const hub = conn?.hub;
  const project = conn?.projects.find(p => p.Id === projectId);

  useEffect(() => {
    if (!hub || conn?.connectionState !== 'connected') return;
    clearOutput();
    setPhase('loading');

    if (settleTimerRef.current) clearTimeout(settleTimerRef.current);
    settleTimerRef.current = setTimeout(() => setPhase('ready'), 800);

    hub.subscribeProject(projectId, 0).catch(console.error);
    return () => {
      hub.unsubscribeProject(projectId).catch(console.error);
      if (settleTimerRef.current) clearTimeout(settleTimerRef.current);
    };
  }, [hub, projectId, conn?.connectionState, clearOutput]);

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

  // MCP badges — try to load effective MCP servers (available when PR4+ merged)
  const [mcpServers, setMcpServers] = useState<string[]>([]);
  useEffect(() => {
    if (!hub || !project?.ProfileName || !project?.RootName) return;
    // getEffectiveMcpServers may not exist yet — gracefully handle
    const fn = (hub as unknown as Record<string, unknown>)['getEffectiveMcpServers'];
    if (typeof fn !== 'function') return;
    (fn as (p: string, r: string) => Promise<Record<string, unknown>>)
      .call(hub, project.ProfileName, project.RootName)
      .then(result => setMcpServers(Object.keys(result)))
      .catch(() => {});
  }, [hub, project?.ProfileName, project?.RootName]);

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

  const handleDelete = async () => {
    if (!hub || !confirm(`Delete project "${projectName}"?`)) return;
    try { await hub.deleteProject(projectId, state === 'Running'); } catch (err) { console.error(err); }
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
          <span className="project-header-name">{projectName}</span>
          {(project?.ProfileName || project?.RootName) && (
            <span className="project-header-root">
              {project?.ProfileName && project.ProfileName !== 'Default' ? project.ProfileName : ''}
              {project?.ProfileName && project.ProfileName !== 'Default' && project?.RootName ? ' / ' : ''}
              {project?.RootName ?? ''}
            </span>
          )}
          {mcpServers.length > 0 && (
            <div className="mcp-badges">
              {mcpServers.length <= 3 ? (
                mcpServers.map(name => <span key={name} className="mcp-badge">{name}</span>)
              ) : (
                <>
                  {mcpServers.slice(0, 2).map(name => <span key={name} className="mcp-badge">{name}</span>)}
                  <span className="mcp-badge mcp-badge-count" title={mcpServers.join(', ')}>+{mcpServers.length - 2}</span>
                </>
              )}
            </div>
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
          <button className="delete-btn" onClick={handleDelete} title="Delete project">
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <polyline points="3 6 5 6 21 6" /><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" />
            </svg>
          </button>
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
        <button className="btn btn-primary" onClick={handleSendInput} disabled={!canSendInput || !inputText.trim()}>
          Send
        </button>
      </div>
    </div>
  );
}
