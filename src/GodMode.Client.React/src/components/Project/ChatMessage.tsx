import type { ClaudeMessage } from '../../signalr/types';

interface Props {
  message: ClaudeMessage;
}

export function ChatMessage({ message }: Props) {
  // System messages
  if (message.type === 'system') {
    return (
      <div className="chat-msg chat-msg-system">
        <span className="chat-msg-badge">SYS</span>
        <span className="chat-msg-summary">{message.summary}</span>
      </div>
    );
  }

  // Result messages
  if (message.type === 'result') {
    return (
      <div className="chat-msg chat-msg-result">
        <span className="chat-msg-badge">DONE</span>
        <span className="chat-msg-summary">{message.summary}</span>
      </div>
    );
  }

  // User messages
  if (message.isUserMessage) {
    return (
      <div className="chat-msg chat-msg-user">
        <span className="chat-msg-badge">YOU</span>
        <div className="chat-msg-content">
          {message.contentSummary || message.summary}
        </div>
      </div>
    );
  }

  // Assistant messages
  if (message.type === 'assistant') {
    return (
      <div className="chat-msg chat-msg-assistant">
        <span className="chat-msg-badge">AI</span>
        <div className="chat-msg-content">
          {message.hasContentItems ? (
            message.contentItems.map((item, i) => (
              <ContentItem key={i} item={item} />
            ))
          ) : (
            <span>{message.summary}</span>
          )}
        </div>
      </div>
    );
  }

  // Error or unknown
  return (
    <div className="chat-msg chat-msg-error">
      <span className="chat-msg-badge">ERR</span>
      <span className="chat-msg-summary">{message.summary || message.typeDisplay}</span>
    </div>
  );
}

function ContentItem({ item }: { item: ClaudeMessage['contentItems'][number] }) {
  if (item.type === 'text') {
    return <div className="content-text">{item.summary}</div>;
  }

  if (item.type === 'tool_use') {
    return (
      <div className={`content-tool ${item.isError ? 'content-tool-error' : ''}`}>
        <div className="content-tool-header">
          <span className="content-tool-name">{item.toolName}</span>
          {item.toolFilePath && (
            <span className="content-tool-path">{item.toolFilePath}</span>
          )}
        </div>
        {item.toolCommand && (
          <pre className="content-tool-command">{item.toolCommand}</pre>
        )}
        {item.toolDescription && (
          <div className="content-tool-desc">{item.toolDescription}</div>
        )}
      </div>
    );
  }

  if (item.type === 'tool_result') {
    return (
      <div className={`content-tool-result ${item.isError ? 'content-tool-error' : ''}`}>
        <pre className="content-tool-output">{item.summary}</pre>
      </div>
    );
  }

  return <div className="content-unknown">[{item.type}] {item.summary}</div>;
}
