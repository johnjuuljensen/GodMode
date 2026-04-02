import { useState } from 'react';
import { useAppStore } from '../../store';
import { isMaui } from '../../services/hostApi';
import '../settings-common.css';

export function AddServer() {
  const addServer = useAppStore(s => s.addServer);

  const [type, setType] = useState<'local' | 'github'>('local');
  const [url, setUrl] = useState('http://localhost:31337');
  const [displayName, setDisplayName] = useState('');
  const [username, setUsername] = useState('');
  const [accessToken, setAccessToken] = useState('');
  const [saving, setSaving] = useState(false);

  const handleSave = async () => {
    if (type === 'local' && !url.trim()) return;
    if (type === 'github' && (!username.trim() || !accessToken.trim())) return;

    setSaving(true);
    try {
      await addServer({
        Type: type,
        Url: type === 'local' ? url.trim() : '',
        DisplayName: displayName.trim() || (type === 'local' ? url.trim() : `GitHub (${username.trim()})`),
        Username: type === 'github' ? username.trim() : null,
        AccessToken: accessToken.trim() || null,
      });
    } catch (err) {
      console.error('Failed to add server:', err);
    } finally {
      setSaving(false);
    }
  };

  return (
    <>
      <div className="settings-header">
        <h2>Add Server</h2>
      </div>

      {isMaui && (
        <div className="form-group">
          <label>Type</label>
          <select value={type} onChange={e => setType(e.target.value as 'local' | 'github')}>
            <option value="local">Local Server</option>
            <option value="github">GitHub Codespaces</option>
          </select>
        </div>
      )}

      {type === 'local' ? (
        <>
          <div className="form-group">
            <label>Server URL</label>
            <input type="text" value={url} onChange={e => setUrl(e.target.value)} placeholder="http://localhost:31337" autoFocus />
          </div>
          <div className="form-group">
            <label>Display Name</label>
            <input type="text" value={displayName} onChange={e => setDisplayName(e.target.value)} placeholder="My Server" />
          </div>
          <div className="form-group">
            <label>Access Token (optional)</label>
            <input type="password" value={accessToken} onChange={e => setAccessToken(e.target.value)} />
          </div>
        </>
      ) : (
        <>
          <div className="form-group">
            <label>GitHub Username</label>
            <input type="text" value={username} onChange={e => setUsername(e.target.value)} placeholder="johndoe" autoFocus />
          </div>
          <div className="form-group">
            <label>Personal Access Token</label>
            <input type="password" value={accessToken} onChange={e => setAccessToken(e.target.value)} placeholder="ghp_..." />
          </div>
          <div className="form-group">
            <label>Display Name</label>
            <input type="text" value={displayName} onChange={e => setDisplayName(e.target.value)} placeholder="My Codespaces" />
          </div>
        </>
      )}

      <div className="settings-form-actions">
        <button className="btn btn-primary" onClick={handleSave} disabled={saving}>
          {saving ? 'Adding...' : 'Add Server'}
        </button>
      </div>
    </>
  );
}
