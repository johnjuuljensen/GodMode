import type { ServerState } from '../../store';

interface Props {
  server: ServerState;
  onConnect: () => void;
  onDisconnect: () => void;
  onEdit: () => void;
  onRemove: () => void;
}

export function ServerItem({ server, onConnect, onDisconnect, onEdit, onRemove }: Props) {
  const { registration, connectionState } = server;

  const handleClick = () => {
    if (connectionState === 'disconnected') {
      onConnect();
    }
    // When connected or connecting, clicking just toggles project list visibility (no-op, list is already shown)
  };

  return (
    <div className="server-item" onClick={handleClick}>
      <div className={`server-dot ${connectionState}`} />
      <div className="server-info">
        <div className="server-name">
          {registration.displayName || registration.url}
        </div>
        <div className="server-url">{registration.url}</div>
      </div>
      <div className="server-actions">
        {connectionState === 'connected' && (
          <button
            className="server-action-btn"
            onClick={(e) => { e.stopPropagation(); onDisconnect(); }}
            title="Disconnect"
          >
            ⏏
          </button>
        )}
        {connectionState === 'connecting' || connectionState === 'reconnecting' ? (
          <span className="server-action-btn">{connectionState}…</span>
        ) : null}
        <button
          className="server-action-btn"
          onClick={(e) => { e.stopPropagation(); onEdit(); }}
          title="Server settings"
        >
          ⚙
        </button>
        {connectionState === 'disconnected' && (
          <button
            className="server-action-btn server-remove-btn"
            onClick={(e) => { e.stopPropagation(); onRemove(); }}
            title="Remove server"
          >
            ×
          </button>
        )}
      </div>
    </div>
  );
}
