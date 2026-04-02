import { useState, useCallback } from 'react';
import { useAppStore } from '../../store';
import type { SharedRootPreview } from '../../signalr/types';
import { IconBtn, ICON_EDIT, ICON_COPY, RowDelete } from '../settings-shared';
import '../settings-common.css';

const refresh = () => useAppStore.getState().refreshFirstConnected();

type View = 'list' | 'create' | 'import' | 'edit';

export function RootManager() {
  const serverConnections = useAppStore(s => s.serverConnections);
  const profileFilter = useAppStore(s => s.profileFilter);

  const conn = serverConnections.find(c => c.connectionState === 'connected');
  const hub = conn?.hub;
  const allRoots = conn?.roots ?? [];
  const profiles = conn?.profiles ?? [];

  const roots = profileFilter !== 'All'
    ? allRoots.filter(r => (r.ProfileName ?? 'Default').toLowerCase() === profileFilter.toLowerCase())
    : allRoots;

  const [view, setView] = useState<View>('list');
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  // Create form
  const [newRootName, setNewRootName] = useState('');
  const [newRootProfile, setNewRootProfile] = useState('');
  const [newConfigJson, setNewConfigJson] = useState('{\n  "description": "",\n  "nameTemplate": "{name}",\n  "promptTemplate": "{prompt}"\n}');

  // Edit form
  const [editRootName, setEditRootName] = useState('');
  const [editRootProfile, setEditRootProfile] = useState('');
  const [editConfigJson, setEditConfigJson] = useState('');

  // Import form
  const [gitUrl, setGitUrl] = useState('');
  const [gitPath, setGitPath] = useState('');
  const [gitRef, setGitRef] = useState('');
  const [importPreview, setImportPreview] = useState<SharedRootPreview | null>(null);
  const [importRootName, setImportRootName] = useState('');

  const goList = () => { setView('list'); setError(null); };

  const handleCreate = async () => {
    if (!hub || !newRootName.trim()) return;
    setError(null);
    setLoading(true);
    try {
      let configJson = newConfigJson;
      if (newRootProfile) {
        try {
          const parsed = JSON.parse(configJson);
          parsed.profileName = newRootProfile;
          configJson = JSON.stringify(parsed, null, 2);
        } catch { /* leave as-is if JSON is invalid */ }
      }
      const files: Record<string, string> = { 'config.json': configJson };
      await hub.createRoot(newRootName.trim(), { Files: files });
      await refresh();
      setNewRootName('');
      setNewRootProfile('');
      setNewConfigJson('{\n  "description": "",\n  "nameTemplate": "{name}",\n  "promptTemplate": "{prompt}"\n}');
      goList();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to create root');
    } finally {
      setLoading(false);
    }
  };

  const handleEdit = async (profileName: string, rootName: string) => {
    if (!hub) return;
    setError(null);
    setLoading(true);
    try {
      const preview = await hub.getRootPreview(profileName, rootName);
      if (!preview) { setError('Could not read root'); setLoading(false); return; }
      setEditRootName(rootName);
      setEditRootProfile(profileName);
      setEditConfigJson(preview.Files['config.json'] ?? '{}');
      setView('edit');
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load root');
    } finally {
      setLoading(false);
    }
  };

  const handleSaveEdit = async () => {
    if (!hub || !editRootName) return;
    setError(null);
    setLoading(true);
    try {
      const preview = await hub.getRootPreview(editRootProfile, editRootName);
      if (!preview) { setError('Could not read root'); setLoading(false); return; }
      preview.Files['config.json'] = editConfigJson;
      await hub.updateRoot(editRootProfile, editRootName, preview);
      await refresh();
      goList();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to save root');
    } finally {
      setLoading(false);
    }
  };

  const handleChangeProfile = async (rootProfileName: string, rootName: string, newProfile: string) => {
    if (!hub) return;
    setError(null);
    try {
      const preview = await hub.getRootPreview(rootProfileName, rootName);
      if (!preview) return;
      const configStr = preview.Files['config.json'];
      if (configStr) {
        try {
          const parsed = JSON.parse(configStr);
          if (newProfile) parsed.profileName = newProfile;
          else delete parsed.profileName;
          preview.Files['config.json'] = JSON.stringify(parsed, null, 2);
        } catch { /* skip */ }
      }
      await hub.updateRoot(rootProfileName, rootName, preview);
      await refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to change profile');
    }
  };

  const handleDelete = async (profileName: string, rootName: string) => {
    if (!hub) return;
    setError(null);
    try {
      await hub.deleteRoot(profileName, rootName, true);
      await refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to delete root');
    }
  };

  const handleCopy = async (profileName: string, rootName: string) => {
    if (!hub) return;
    setError(null);
    try {
      const preview = await hub.getRootPreview(profileName, rootName);
      if (!preview) { setError('Could not read root'); return; }
      await hub.createRoot(`${rootName}-copy`, preview, profileName);
      await refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to copy root');
    }
  };

  const handlePreviewGit = useCallback(async () => {
    if (!hub || !gitUrl.trim()) return;
    setError(null);
    setLoading(true);
    try {
      const preview = await hub.previewImportFromGit(gitUrl.trim(), gitPath.trim() || null, gitRef.trim() || null);
      setImportPreview(preview);
      setImportRootName(preview.Manifest.Name);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to preview import');
    } finally {
      setLoading(false);
    }
  }, [hub, gitUrl, gitPath, gitRef]);

  const handleInstall = async () => {
    if (!hub || !importPreview || !importRootName.trim()) return;
    setError(null);
    setLoading(true);
    try {
      await hub.installSharedRoot(importRootName.trim(), importPreview);
      await refresh();
      setImportPreview(null);
      setGitUrl('');
      setGitPath('');
      setGitRef('');
      goList();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to install root');
    } finally {
      setLoading(false);
    }
  };

  // ── Detail views (create / edit / import) ──
  if (view === 'create') {
    return (
      <>
        <div className="settings-header">
          <button className="settings-back-link" onClick={goList}>
            <svg width={16} height={16} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.5} strokeLinecap="round" strokeLinejoin="round"><path d="M10 4L6 8l4 4"/></svg>
            Roots
          </button>
        </div>
        <div className="settings-header"><h2>New Root</h2></div>
        {error && <div className="settings-error">{error}</div>}
        <div className="form-group">
          <label>Root Name</label>
          <input type="text" value={newRootName} onChange={e => setNewRootName(e.target.value)} placeholder="my-root" />
        </div>
        <div className="form-group">
          <label>Profile</label>
          <select value={newRootProfile} onChange={e => setNewRootProfile(e.target.value)}>
            <option value="">All profiles</option>
            {profiles.map(p => <option key={p.Name} value={p.Name}>{p.Name}</option>)}
          </select>
        </div>
        <div className="form-group">
          <label>config.json</label>
          <textarea className="form-textarea" rows={8} value={newConfigJson} onChange={e => setNewConfigJson(e.target.value)} />
        </div>
        <div className="settings-form-actions">
          <button className="btn btn-primary" onClick={handleCreate} disabled={loading || !newRootName.trim()}>
            {loading ? 'Creating...' : 'Create Root'}
          </button>
        </div>
      </>
    );
  }

  if (view === 'edit') {
    return (
      <>
        <div className="settings-header">
          <button className="settings-back-link" onClick={goList}>
            <svg width={16} height={16} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.5} strokeLinecap="round" strokeLinejoin="round"><path d="M10 4L6 8l4 4"/></svg>
            Roots
          </button>
        </div>
        <div className="settings-header"><h2>Edit: {editRootName}</h2></div>
        {error && <div className="settings-error">{error}</div>}
        <div className="form-group">
          <label>config.json</label>
          <textarea className="form-textarea" rows={12} value={editConfigJson} onChange={e => setEditConfigJson(e.target.value)} />
        </div>
        <div className="settings-form-actions">
          <button className="btn btn-primary" onClick={handleSaveEdit} disabled={loading}>
            {loading ? 'Saving...' : 'Save Changes'}
          </button>
        </div>
      </>
    );
  }

  if (view === 'import') {
    return (
      <>
        <div className="settings-header">
          <button className="settings-back-link" onClick={goList}>
            <svg width={16} height={16} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.5} strokeLinecap="round" strokeLinejoin="round"><path d="M10 4L6 8l4 4"/></svg>
            Roots
          </button>
        </div>
        <div className="settings-header"><h2>Import from Git</h2></div>
        {error && <div className="settings-error">{error}</div>}
        <div className="form-group">
          <label>Git URL</label>
          <input type="text" value={gitUrl} onChange={e => setGitUrl(e.target.value)} placeholder="https://github.com/org/repo.git" />
        </div>
        <div className="form-group">
          <label>Path (optional)</label>
          <input type="text" value={gitPath} onChange={e => setGitPath(e.target.value)} placeholder="subdirectory/path" />
        </div>
        <div className="form-group">
          <label>Ref (optional)</label>
          <input type="text" value={gitRef} onChange={e => setGitRef(e.target.value)} placeholder="main, v1.0, etc." />
        </div>
        {importPreview && (
          <div className="form-group">
            <label>Install as</label>
            <input type="text" value={importRootName} onChange={e => setImportRootName(e.target.value)} />
            <div className="form-description">
              {Object.keys(importPreview.Preview.Files).length} files: {Object.keys(importPreview.Preview.Files).join(', ')}
            </div>
          </div>
        )}
        <div className="settings-form-actions">
          {!importPreview ? (
            <button className="btn btn-primary" onClick={handlePreviewGit} disabled={loading || !gitUrl.trim()}>
              {loading ? 'Loading...' : 'Preview'}
            </button>
          ) : (
            <button className="btn btn-primary" onClick={handleInstall} disabled={loading || !importRootName.trim()}>
              {loading ? 'Installing...' : 'Install'}
            </button>
          )}
        </div>
      </>
    );
  }

  // ── List view ──
  return (
    <>
      <div className="settings-header">
        <h2>Roots</h2>
        <div style={{ display: 'flex', gap: 6 }}>
          <button className="settings-add-btn" onClick={() => setView('import')}>Import</button>
          <button className="settings-add-btn" onClick={() => setView('create')}>
            <span className="plus">+</span> Create
          </button>
        </div>
      </div>

      {error && <div className="settings-error">{error}</div>}

      {roots.length === 0 ? (
        <div className="settings-empty">No roots configured. Create or import one.</div>
      ) : (
        <>
          <div className="settings-list">
            {roots.map(r => (
              <div key={`${r.ProfileName}/${r.Name}`} className="settings-item">
                <div className="settings-item-info">
                  <div className="settings-item-name">{r.Name}</div>
                  {r.Description && <div className="settings-item-desc">{r.Description}</div>}
                </div>
                <div className="settings-item-actions">
                  <select
                    className="settings-badge"
                    style={{ cursor: 'pointer', background: 'var(--glass)' }}
                    value={r.ProfileName === 'Default' ? '' : (r.ProfileName ?? '')}
                    onChange={e => handleChangeProfile(r.ProfileName ?? 'Default', r.Name, e.target.value)}
                  >
                    <option value="">All profiles</option>
                    {profiles.map(p => <option key={p.Name} value={p.Name}>{p.Name}</option>)}
                  </select>
                  <IconBtn title="Edit" svg={ICON_EDIT} onClick={() => handleEdit(r.ProfileName ?? 'Default', r.Name)} />
                  <IconBtn title="Copy" svg={ICON_COPY} onClick={() => handleCopy(r.ProfileName ?? 'Default', r.Name)} />
                  <RowDelete onDelete={() => handleDelete(r.ProfileName ?? 'Default', r.Name)} />
                </div>
              </div>
            ))}
          </div>
          <div className="settings-count">{roots.length} root{roots.length !== 1 ? 's' : ''}</div>
        </>
      )}
    </>
  );
}
