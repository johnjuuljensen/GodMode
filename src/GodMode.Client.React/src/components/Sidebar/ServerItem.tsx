import type { ServerState } from '../../store';

interface Props {
  server: ServerState;
  onConnect: () => void;
  onDisconnect: () => void;
  onEdit: () => void;
}

export function ServerItem({ server, onConnect, onDisconnect, onEdit }: Props) {
  const { registration, connectionState } = server;

  return (
    <div className="server-item" onClick={onEdit}>
      <div className={`server-dot ${connectionState}`} />
      <div className="server-info">
        <div className="server-name">
          {registration.displayName || registration.url}
        </div>
        <div className="server-url">{registration.url}</div>
      </div>
      <div className="server-actions">
        {connectionState === 'disconnected' ? (
          <button
            className="server-action-btn"
            onClick={(e) => { e.stopPropagation(); onConnect(); }}
          >
            connect
          </button>
        ) : connectionState === 'connected' ? (
          <button
            className="server-action-btn"
            onClick={(e) => { e.stopPropagation(); onDisconnect(); }}
          >
            disconnect
          </button>
        ) : (
          <span className="server-action-btn">{connectionState}...</span>
        )}
      </div>
    </div>
  );
}
