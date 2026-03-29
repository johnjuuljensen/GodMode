import { useState } from 'react';
import { useAppStore } from '../../store';
import './ProfileSettings.css';

export function CreateProfile() {
  const servers = useAppStore(s => s.servers);
  const createProfileServerId = useAppStore(s => s.createProfileServerId);
  const setShowCreateProfile = useAppStore(s => s.setShowCreateProfile);
  const refreshProjects = useAppStore(s => s.refreshProjects);

  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);

  // If multiple servers, let user pick; otherwise use the first connected one
  const connectedServers = servers.filter(s => s.connectionState === 'connected');

  const [selectedServerId, setSelectedServerId] = useState<string>(
    createProfileServerId ?? connectedServers[0]?.registration.url ?? ''
  );

  const hub = servers.find(s => s.registration.url === selectedServerId)?.hub;

  const handleCreate = async () => {
    if (!hub || !name.trim()) return;
    setError(null);
    setCreating(true);
    try {
      await hub.createProfile(name.trim(), description.trim() || null);
      await refreshProjects(selectedServerId);
      setShowCreateProfile(false);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to create profile');
    } finally {
      setCreating(false);
    }
  };

  const close = () => setShowCreateProfile(false);

  return (
    <div className="modal-overlay" onClick={close}>
      <div className="modal" style={{ maxWidth: 420 }} onClick={e => e.stopPropagation()}>
        <h2>Create Profile</h2>

        {connectedServers.length > 1 && (
          <div className="form-group">
            <label>Server</label>
            <select
              value={selectedServerId}
              onChange={e => setSelectedServerId(e.target.value)}
            >
              {connectedServers.map(s => (
                <option key={s.registration.url} value={s.registration.url}>
                  {s.registration.url}
                </option>
              ))}
            </select>
          </div>
        )}

        <div className="form-group">
          <label>Profile Name</label>
          <input
            type="text"
            value={name}
            onChange={e => setName(e.target.value)}
            placeholder="e.g. Production, Staging, MyProject"
            autoFocus
          />
        </div>

        <div className="form-group">
          <label>Description <span className="form-description">(optional)</span></label>
          <input
            type="text"
            value={description}
            onChange={e => setDescription(e.target.value)}
            placeholder="Optional description for this profile"
          />
        </div>

        {error && <div className="form-error">{error}</div>}

        <div className="btn-group">
          <button
            className="btn btn-primary"
            onClick={handleCreate}
            disabled={!name.trim() || creating}
          >
            {creating ? 'Creating...' : 'Create'}
          </button>
          <button className="btn btn-secondary" onClick={close}>
            Cancel
          </button>
        </div>
      </div>
    </div>
  );
}
