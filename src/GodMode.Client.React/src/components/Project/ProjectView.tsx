import { useEffect, useRef, useState } from 'react';
import { useAppStore } from '../../store';
import { ChatMessage } from './ChatMessage';
import './ProjectView.css';

interface Props {
  serverIndex: number;
  projectId: string;
}

export function ProjectView({ serverIndex, projectId }: Props) {
  const server = useAppStore(s => s.servers[serverIndex]);
  const outputMessages = useAppStore(s => s.outputMessages);
  const clearOutput = useAppStore(s => s.clearOutput);
  const [inputText, setInputText] = useState('');
  const [projectName, setProjectName] = useState('');
  const messagesEndRef = useRef<HTMLDivElement>(null);

  const hub = server?.hub;
  const project = server?.projects.find(p => p.Id === projectId);

  // Subscribe to project output on mount
  useEffect(() => {
    if (!hub || server?.connectionState !== 'connected') return;
    clearOutput();

    hub.subscribeProject(projectId, 0).catch(console.error);
    return () => {
      hub.unsubscribeProject(projectId).catch(console.error);
    };
  }, [hub, projectId, server?.connectionState, clearOutput]);

  // Update project name
  useEffect(() => {
    if (project) setProjectName(project.Name);
  }, [project]);

  // Auto-scroll to bottom
  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [outputMessages.length]);

  const handleSendInput = async () => {
    if (!inputText.trim() || !hub) return;
    const text = inputText;
    setInputText('');
    try {
      await hub.sendInput(projectId, text);
    } catch (err) {
      console.error('Failed to send input:', err);
    }
  };

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

  const state = project?.State ?? 'Idle';
  const canSendInput = state === 'WaitingInput';
  const canResume = state === 'Stopped' || state === 'Idle';
  const canStop = state === 'Running' || state === 'WaitingInput';

  return (
    <div className="project-view">
      <div className="project-header">
        <div className="project-header-info">
          <span className={`project-state-badge ${state}`}>{state}</span>
          <span className="project-header-name">{projectName}</span>
          {project?.RootName && (
            <span className="project-header-root">{project.RootName}</span>
          )}
        </div>
        <div className="project-header-actions">
          {canStop && (
            <button className="btn btn-secondary" onClick={handleStop}>Stop</button>
          )}
          {canResume && (
            <button className="btn btn-primary" onClick={handleResume}>Resume</button>
          )}
        </div>
      </div>

      <div className="project-messages">
        {outputMessages.length === 0 ? (
          <div className="project-messages-empty">
            {server?.connectionState === 'connected'
              ? 'Waiting for output...'
              : 'Not connected'}
          </div>
        ) : (
          outputMessages.map((msg, i) => (
            <ChatMessage key={i} message={msg} />
          ))
        )}
        <div ref={messagesEndRef} />
      </div>

      {project?.CurrentQuestion && (
        <div className="project-question">
          <div className="project-question-text">{project.CurrentQuestion}</div>
        </div>
      )}

      <div className="project-input-bar">
        <input
          type="text"
          className="project-input"
          value={inputText}
          onChange={e => setInputText(e.target.value)}
          onKeyDown={handleKeyDown}
          placeholder={canSendInput ? 'Type a response...' : 'Waiting...'}
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
