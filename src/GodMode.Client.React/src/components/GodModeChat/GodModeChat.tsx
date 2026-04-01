import { useState, useRef, useEffect } from 'react';
import { useAppStore } from '../../store';
import type { ChatResponseMessage } from '../../signalr/types';
import './GodModeChat.css';

/** Stored chat entry — either a user message or a server response */
export type GodModeChatEntry =
  | { role: 'user'; content: string }
  | { role: 'server'; message: ChatResponseMessage };

export function GodModeChat() {
  const setShowGodModeChat = useAppStore(s => s.setShowGodModeChat);
  const messages = useAppStore(s => s.godModeChatMessages);
  const loading = useAppStore(s => s.godModeChatLoading);
  const setLoading = useAppStore(s => s.setGodModeChatLoading);
  const clearChat = useAppStore(s => s.clearGodModeChat);
  const appendMessage = useAppStore(s => s.appendGodModeChatMessage);
  const serverConnections = useAppStore(s => s.serverConnections);

  const [input, setInput] = useState('');
  const messagesEndRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLTextAreaElement>(null);

  const hub = serverConnections.find(c => c.connectionState === 'connected')?.hub;

  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages]);

  useEffect(() => {
    inputRef.current?.focus();
  }, []);

  const handleSend = async () => {
    const text = input.trim();
    if (!text || !hub || loading) return;

    appendMessage({ role: 'user', content: text });
    setInput('');
    setLoading(true);
    try {
      await hub.sendChatMessage(text);
    } catch (err) {
      console.error('Failed to send chat message:', err);
      setLoading(false);
    }
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      handleSend();
    }
  };

  const handleClear = async () => {
    clearChat();
    try { await hub?.clearChatHistory(); } catch { /* ignore */ }
  };

  return (
    <div className="godmode-chat">
      <div className="godmode-chat-header">
        <div className="godmode-chat-header-left">
          <span className="godmode-chat-logo">GM</span>
          <span className="godmode-chat-title">GodMode Chat</span>
        </div>
        <div className="godmode-chat-header-right">
          <button className="godmode-chat-header-btn" onClick={handleClear} title="Clear chat">
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <polyline points="3 6 5 6 21 6" /><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" />
            </svg>
          </button>
          <button className="godmode-chat-header-btn" onClick={() => setShowGodModeChat(false)} title="Close">
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <line x1="18" y1="6" x2="6" y2="18" /><line x1="6" y1="6" x2="18" y2="18" />
            </svg>
          </button>
        </div>
      </div>

      <div className="godmode-chat-messages">
        {messages.length === 0 && (
          <div className="godmode-chat-welcome">
            <div className="godmode-chat-welcome-logo">GM</div>
            <h3>Welcome to GodMode Chat</h3>
            <p>Ask me to manage your roots, profiles, MCP servers, and projects.</p>
            <div className="godmode-chat-suggestions">
              <button onClick={() => setInput('List all my profiles and roots')}>List profiles & roots</button>
              <button onClick={() => setInput('Create a new profile called "experiments"')}>Create a profile</button>
              <button onClick={() => setInput('Generate a root for a React + TypeScript project')}>Generate a root</button>
            </div>
          </div>
        )}
        {messages.map((entry, i) => (
          <ChatEntry key={i} entry={entry} />
        ))}
        {loading && (
          <div className="godmode-chat-thinking">
            <span className="godmode-chat-thinking-dot" />
            <span className="godmode-chat-thinking-dot" />
            <span className="godmode-chat-thinking-dot" />
          </div>
        )}
        <div ref={messagesEndRef} />
      </div>

      <div className="godmode-chat-input-area">
        <textarea
          ref={inputRef}
          className="godmode-chat-input"
          value={input}
          onChange={e => setInput(e.target.value)}
          onKeyDown={handleKeyDown}
          placeholder="Ask GodMode anything..."
          rows={1}
          disabled={!hub}
        />
        <button
          className="godmode-chat-send"
          onClick={handleSend}
          disabled={!input.trim() || !hub || loading}
          title="Send"
        >
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <line x1="22" y1="2" x2="11" y2="13" /><polygon points="22 2 15 22 11 13 2 9 22 2" />
          </svg>
        </button>
      </div>
    </div>
  );
}

function ChatEntry({ entry }: { entry: GodModeChatEntry }) {
  if (entry.role === 'user') {
    return (
      <div className="godmode-chat-bubble godmode-chat-user">
        {entry.content}
      </div>
    );
  }

  const { Type, Content, ToolName } = entry.message;

  if (Type === 'ToolCall') {
    return (
      <div className="godmode-chat-bubble godmode-chat-tool">
        <span className="godmode-chat-tool-icon">
          <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z" />
          </svg>
        </span>
        <span className="godmode-chat-tool-label">{ToolName ?? 'tool'}</span>
      </div>
    );
  }

  if (Type === 'ToolResult') {
    return (
      <div className="godmode-chat-bubble godmode-chat-tool-result">
        <span className="godmode-chat-tool-result-label">{ToolName}</span>
        <pre className="godmode-chat-tool-result-content">{Content}</pre>
      </div>
    );
  }

  if (Type === 'Error') {
    return (
      <div className="godmode-chat-bubble godmode-chat-error">
        {Content}
      </div>
    );
  }

  // Text response from assistant
  return (
    <div className="godmode-chat-bubble godmode-chat-text">
      {Content}
    </div>
  );
}
