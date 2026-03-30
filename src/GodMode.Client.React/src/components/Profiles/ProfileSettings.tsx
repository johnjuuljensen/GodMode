import { useState } from 'react';
import { useAppStore } from '../../store';
import './ProfileSettings.css';

export function ProfileSettings() {
  const setShowProfileSettings = useAppStore(s => s.setShowProfileSettings);
  const serverConnections = useAppStore(s => s.serverConnections);

  const conn = serverConnections.find(c => c.connectionState === 'connected');
  const hub = conn?.hub;
  const profiles = conn?.profiles ?? [];

  const [newName, setNewName] = useState('');
  const [newDescription, setNewDescription] = useState('');
  const [error, setError] = useState<string | null>(null);

  const handleCreate = async () => {
    if (!hub || !newName.trim()) return;
    setError(null);
    try {
      await hub.createProfile(newName.trim(), newDescription.trim() || null);
      setNewName('');
      setNewDescription('');
      // Refresh will happen via server reconnect/reload
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to create profile');
    }
  };

  return (
    <div className="modal-overlay" onClick={() => setShowProfileSettings(false)}>
      <div className="modal profile-settings-modal" onClick={e => e.stopPropagation()}>
        <h2>Profiles</h2>

        {error && <div className="form-error">{error}</div>}

        <div className="profile-list">
          {profiles.map(p => (
            <div key={p.Name} className="profile-item">
              <div className="profile-item-name">{p.Name}</div>
              {p.Description && <div className="profile-item-desc">{p.Description}</div>}
            </div>
          ))}
        </div>

        <div className="profile-add-section">
          <h3>Create Profile</h3>
          <div className="form-group">
            <label>Name</label>
            <input type="text" value={newName} onChange={e => setNewName(e.target.value)} placeholder="Profile name" />
          </div>
          <div className="form-group">
            <label>Description</label>
            <input type="text" value={newDescription} onChange={e => setNewDescription(e.target.value)} placeholder="Optional description" />
          </div>
          <div className="form-actions">
            <button className="btn btn-secondary" onClick={() => setShowProfileSettings(false)}>Close</button>
            <button className="btn btn-primary" onClick={handleCreate} disabled={!newName.trim()}>Create</button>
          </div>
        </div>
      </div>
    </div>
  );
}
