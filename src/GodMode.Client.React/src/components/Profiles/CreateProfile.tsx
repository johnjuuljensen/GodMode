import { useState } from 'react';
import { useAppStore } from '../../store';
import './ProfileSettings.css';

export function CreateProfile() {
  const servers = useAppStore(s => s.servers);
  const createProfileServerIndex = useAppStore(s => s.createProfileServerIndex);
  const setShowCreateProfile = useAppStore(s => s.setShowCreateProfile);
  const refreshProjects = useAppStore(s => s.refreshProjects);

  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);

  // If multiple servers, let user pick; otherwise use the first connected one
  const connectedServers = servers
    .map((s, i) => ({ server: s, index: i }))
    .filter(s => s.server.connectionState === 'connected');

  const [selectedServerIndex, setSelectedServerIndex] = useState<number>(
    createProfileServerIndex ?? connectedServers[0]?.index ?? 0
  );

  const hub = servers[selectedServerIndex]?.hub;

  const handleCreate = async () => {
    if (!hub || !name.trim()) return;
    setError(null);
    setCreating(true);
    try {
      await hub.createProfile(name.trim(), description.trim() || null);
      await refreshProjects(selectedServerIndex);
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
              value={selectedServerIndex}
              onChange={e => setSelectedServerIndex(Number(e.target.value))}
            >
              {connectedServers.map(s => (
                <option key={s.index} value={s.index}>
                  {s.server.registration.url}
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
