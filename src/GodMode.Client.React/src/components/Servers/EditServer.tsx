import { useState } from 'react';
import { useAppStore } from '../../store';

interface Props {
  serverId: string;
}

export function EditServer({ serverId }: Props) {
  const server = useAppStore(s => s.servers.find(sv => sv.registration.url === serverId));
  const updateServer = useAppStore(s => s.updateServer);
  const removeServer = useAppStore(s => s.removeServer);
  const setEditServerId = useAppStore(s => s.setEditServerId);

  const [url, setUrl] = useState(server?.registration.url ?? '');
  const [displayName, setDisplayName] = useState(server?.registration.displayName ?? '');
  const [accessToken, setAccessToken] = useState(server?.registration.accessToken ?? '');

  if (!server) return null;

  const handleSave = () => {
    if (!url.trim()) return;
    updateServer(serverId, {
      url: url.trim(),
      displayName: displayName.trim() || url.trim(),
      accessToken: accessToken.trim() || undefined,
    });
  };

  const handleDelete = () => {
    if (confirm('Remove this server?')) {
      removeServer(serverId);
    }
  };

  return (
    <div className="modal-overlay" onClick={() => setEditServerId(null)}>
      <div className="modal" onClick={e => e.stopPropagation()}>
        <h2>Edit Server</h2>
        <div className="form-group">
          <label>Server URL</label>
          <input
            type="text"
            value={url}
            onChange={e => setUrl(e.target.value)}
            autoFocus
          />
        </div>
        <div className="form-group">
          <label>Display Name</label>
          <input
            type="text"
            value={displayName}
            onChange={e => setDisplayName(e.target.value)}
          />
        </div>
        <div className="form-group">
          <label>Access Token (optional)</label>
          <input
            type="password"
            value={accessToken}
            onChange={e => setAccessToken(e.target.value)}
          />
        </div>
        <div className="btn-group">
          <button className="btn btn-primary" onClick={handleSave}>Save</button>
          <button className="btn btn-danger" onClick={handleDelete}>Delete</button>
          <button className="btn btn-secondary" onClick={() => setEditServerId(null)}>Cancel</button>
        </div>
      </div>
    </div>
  );
}
