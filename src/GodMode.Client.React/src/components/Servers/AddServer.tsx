import { useState } from 'react';
import { useAppStore } from '../../store';

export function AddServer() {
  const addServer = useAppStore(s => s.addServer);
  const setShowAddServer = useAppStore(s => s.setShowAddServer);

  const [url, setUrl] = useState('http://localhost:31337');
  const [displayName, setDisplayName] = useState('');
  const [accessToken, setAccessToken] = useState('');

  const handleSave = () => {
    if (!url.trim()) return;
    addServer({
      url: url.trim(),
      displayName: displayName.trim() || url.trim(),
      accessToken: accessToken.trim() || undefined,
    });
  };

  return (
    <div className="modal-overlay" onClick={() => setShowAddServer(false)}>
      <div className="modal" onClick={e => e.stopPropagation()}>
        <h2>Add Server</h2>
        <div className="form-group">
          <label>Server URL</label>
          <input
            type="text"
            value={url}
            onChange={e => setUrl(e.target.value)}
            placeholder="http://localhost:31337"
            autoFocus
          />
        </div>
        <div className="form-group">
          <label>Display Name</label>
          <input
            type="text"
            value={displayName}
            onChange={e => setDisplayName(e.target.value)}
            placeholder="My Server"
          />
        </div>
        <div className="form-group">
          <label>Access Token (optional, for GitHub Codespaces)</label>
          <input
            type="password"
            value={accessToken}
            onChange={e => setAccessToken(e.target.value)}
            placeholder="ghp_..."
          />
        </div>
        <div className="btn-group">
          <button className="btn btn-primary" onClick={handleSave}>Add</button>
          <button className="btn btn-secondary" onClick={() => setShowAddServer(false)}>Cancel</button>
        </div>
      </div>
    </div>
  );
}
