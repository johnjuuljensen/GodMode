import { useState } from 'react';
import { useAppStore } from '../../store';
import { DeleteConfirm } from '../settings-shared';
import '../settings-common.css';

const refresh = () => useAppStore.getState().refreshFirstConnected();

const BackArrow = () => (
  <svg width={16} height={16} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.5} strokeLinecap="round" strokeLinejoin="round"><path d="M10 4L6 8l4 4"/></svg>
);

type View = 'list' | 'create';

export function ProfileSettings() {
  const serverConnections = useAppStore(s => s.serverConnections);

  const conn = serverConnections.find(c => c.connectionState === 'connected');
  const hub = conn?.hub;
  const profiles = conn?.profiles ?? [];

  const [view, setView] = useState<View>('list');
  const [newName, setNewName] = useState('');
  const [newDescription, setNewDescription] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [deleteConfirm, setDeleteConfirm] = useState<string | null>(null);

  const goList = () => { setView('list'); setError(null); };

  const handleCreate = async () => {
    if (!hub || !newName.trim()) return;
    setError(null);
    try {
      await hub.createProfile(newName.trim(), newDescription.trim() || null);
      await refresh();
      setNewName('');
      setNewDescription('');
      goList();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to create profile');
    }
  };

  const handleDelete = async (name: string, deleteContents: boolean) => {
    if (!hub) return;
    setError(null);
    setDeleteConfirm(null);
    try {
      await hub.deleteProfile(name, deleteContents);
      await refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to delete profile');
    }
  };

  // ── Create view ──
  if (view === 'create') {
    return (
      <>
        <div className="settings-header">
          <button className="settings-back-link" onClick={goList}><BackArrow /> Profiles</button>
        </div>
        <div className="settings-header"><h2>New Profile</h2></div>
        {error && <div className="settings-error">{error}</div>}
        <div className="form-group">
          <label>Name</label>
          <input type="text" value={newName} onChange={e => setNewName(e.target.value)} placeholder="e.g. production" autoFocus />
        </div>
        <div className="form-group">
          <label>Description</label>
          <input type="text" value={newDescription} onChange={e => setNewDescription(e.target.value)} placeholder="Optional description" />
        </div>
        <div className="settings-form-actions">
          <button className="btn btn-primary" onClick={handleCreate} disabled={!newName.trim()}>Add Profile</button>
        </div>
      </>
    );
  }

  // ── List view ──
  return (
    <>
      <div className="settings-header">
        <h2>Profiles</h2>
        <button className="settings-add-btn" onClick={() => setView('create')}>
          <span className="plus">+</span> Add
        </button>
      </div>

      {error && <div className="settings-error">{error}</div>}

      {profiles.length === 0 ? (
        <div className="settings-empty">No profiles configured. Tap Add to create one.</div>
      ) : (
        <>
          <div className="settings-list">
            {profiles.map(p => (
              <div key={p.Name} className="settings-item">
                <div className="settings-item-info">
                  <div className="settings-item-name">{p.Name}</div>
                  {p.Description && <div className="settings-item-desc">{p.Description}</div>}
                </div>
                <div className="settings-item-actions">
                  {deleteConfirm === p.Name ? (
                    <DeleteConfirm
                      label="Delete roots & projects?"
                      onConfirm={() => handleDelete(p.Name, true)}
                      onCancel={() => setDeleteConfirm(null)}
                    />
                  ) : (
                    <button className="btn btn-danger btn-sm" onClick={() => setDeleteConfirm(p.Name)}>Delete</button>
                  )}
                </div>
              </div>
            ))}
          </div>
          <div className="settings-count">{profiles.length} profile{profiles.length !== 1 ? 's' : ''}</div>
        </>
      )}
    </>
  );
}
